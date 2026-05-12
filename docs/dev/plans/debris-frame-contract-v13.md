# Debris frame contract — v13

**Status:** clean rewrite (replaces all prior drafts), plus shared-module extraction in §3.7.
**Scope:** debris recording and rendering. No changes to non-debris recorder/renderer dispatch, Re-Fly merge/supersede flow, or the loop-anchor live-PID carve-out for non-debris. **One Re-Fly touch** (behaviour-preserving): the Re-Fly cadence resolver is refactored to use the shared `ProximitySamplingCadence` module — same thresholds, same tier semantics, same observable behaviour, just lifted into a reusable module so debris can use it too. See §3.7.
**Compatibility:** none. v11 / v12 recordings are rejected at load.

---

## 1. The design in one paragraph

Every debris sample writes **body-fixed (Absolute) world coordinates as the primary surface, always, at every distance**. When the debris enters within **500 m** of its parent vessel, the recorder *additionally* writes **anchor-local-to-parent offsets** as a secondary surface on the same sample; that Relative section is retained until the debris exits the 550 m hysteresis band. The renderer reads body-fixed coordinates by default, and reads anchor-local through the recording chain when the chain ends in a loop-anchored ancestor (so that looped recordings — e.g. a tanker approach to a station, with debris jettisoned mid-approach — replay correctly against the station's current live orbital pose at each loop iteration). Sampling cadence scales with distance to parent, mirroring Re-Fly's tiered cadence: **full fidelity** inside 250 m, **half fidelity** 250–500 m, **normal adaptive** beyond 500 m.

The relative-to-parent surface is **not experimental** — it has a concrete, mandatory consumer (loop scenarios) plus a sub-metre geometric-accuracy benefit at close range. Storage cost is bounded to the close-range window (enter at 500 m, retain until 550 m for hysteresis, half-rate sampling in the outer 250 m), schema cost is zero (uses existing v7 Relative section layout), and rendering uses the existing chain resolver only when the close-range debris chain reaches a loop-anchored ancestor. Non-loop debris reads the body-fixed primary surface directly.

## 2. What does NOT change

- `Source/Parsek/AnchorDetector.cs` — `ShouldUseRelativeFrame`, `RelativeFrameRangeLimit`. The `candidateRecording.IsDebris → false` exclusion at `:240` stays. **Rewrite** the comment at `:230-239`: the existing wording claims "two-debris anchoring is impossible by construction" because debris-B's contract forces it to anchor on "its own parent (a non-debris parent recording)" — but under v13 with cascade (§3.5), debris-of-debris's parent **is** a debris recording. The `:240` exclusion still correctly excludes debris from the nearest-search candidate list, but its justification is now "the contract path bypasses nearest-search entirely; the rejection is defense-in-depth, not impossibility-by-construction." Update the comment to match.
- `Source/Parsek/RelativeAnchorResolver.cs` — keeps the recorded-anchor DAG resolver. Two surgical changes only, both targeted at the loop-anchored-ancestor case for debris: see §3.4.
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal, ERS/ELS, `RecordingSupersedeRelation`.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged for non-debris. **Debris recordings always have `LoopAnchorVesselId == 0u`** (the v12 invariant is preserved under v13). Loop semantics for debris come from chaining through a loop-anchored ancestor's recording, not from inheriting the field. See §3.4.
- Non-debris recorder frame decisions and the v7 parallel body-fixed list semantics (v12 field name: `absoluteFrames`; v13 field name: `bodyFixedFrames`). Non-debris Relative sections continue to write the parallel body-fixed list per the existing v7 contract.
- Re-Fly tree sampling tier *thresholds* (`ReFlyTreeFullFidelityProximityRangeMeters`, `ReFlyTreeHalfFidelityProximityRangeMeters`) for non-debris. They stay at `250.0` / `500.0` (`FlightRecorder.cs:936-937`). The *resolver* that consumes them is refactored to delegate to the shared module per §3.7 — behaviour preserved.
- `IPlaybackTrajectory.DebrisParentRecordingId` interface property — still exposed for identification / ledger / map UI / diagnostics.
- `SessionMerger.cs` `DebrisParentRecordingId` and body-fixed-frame handling — operates per-section, correct under the new contract.

If a change touches a file outside the debris-specific list in §6, that's a scope violation.

## 3. The contract

### 3.1 Recorder

**Frame transitions, per sample, decided by distance to parent vessel plus current section state:**

| Current state / distance to parent | Section action | `frames` contents | `bodyFixedFrames` contents |
|---|---|---|---|
| Not already Relative and `≤ 500 m` | Open / keep `Relative` (anchored on parent recording) | anchor-local-to-parent offsets (the secondary surface) | body-fixed world coordinates (the primary surface) |
| Already Relative and `(500, 550] m` | Keep `Relative` for hysteresis | anchor-local-to-parent offsets (the secondary surface) | body-fixed world coordinates (the primary surface) |
| Already Relative and `> 550 m` | Close Relative, open `Absolute` | body-fixed world coordinates (the primary surface) | null |
| Not already Relative and `> 500 m` | Open / keep `Absolute` | body-fixed world coordinates (the primary surface) | null |

Hysteresis on the frame-switching boundary: enter Relative at `≤ 500 m`, exit Relative at `> 550 m`. The 50 m hysteresis prevents flapping; playtest-tunable.

Body-fixed coordinates are written on every sample regardless of section type — they live in `frames` when the section is Absolute, in `bodyFixedFrames` when the section is Relative. So the renderer always has a body-fixed surface to read.

**Anchor identity for Relative sections:** the anchor is unconditionally `treeRec.DebrisParentRecordingId` (the parent's `RecordingId`). The `AnchorDetector.FindNearestRecordingAnchor` candidate-list / nearest-search is bypassed for debris — the parent is the anchor, never a sibling debris piece or nearby unrelated vessel.

**Distance source:** parent's live world position when its vessel is **loaded AND unpacked**. The existing `CheckDebrisTTL` at `BackgroundRecorder.cs:1390-1404` ends the debris recording on the same physics tick that observes `parentVessel.packed || !parentVessel.loaded`, so there is no in-window "parent goes packed mid-recording → debris keeps recording at adaptive cadence" transition. The packed-parent / unloaded-parent state retires the debris, not demotes its cadence.

The precise filter for `ResolveDebrisParentDistanceMeters`:

```csharp
Vessel parent = FlightRecorder.FindVesselByPid(ResolveParentVesselPid(debrisRecording));
if (parent == null || !parent.loaded || parent.packed) return double.NaN;
return Vector3d.Distance(parent.GetWorldPos3D(), debrisVessel.GetWorldPos3D());
```

NaN return → `ProximitySamplingCadence.Resolve` returns `None` → cadence falls through to the legacy adaptive cap, but the recording is ended by `CheckDebrisTTL` on the same or next tick anyway.

**Cadence tiers (proximity-driven):** uses the shared `ProximitySamplingCadence` module described in §3.7 (single tier enum across recorders). Debris supplies its own thresholds:

```csharp
internal const double DebrisFullFidelityProximityRangeMeters = 250.0;
internal const double DebrisHalfFidelityProximityRangeMeters = 500.0;  // also: Relative section entry boundary
internal const double DebrisRelativeSectionExitMeters = 550.0;         // hysteresis exit for Relative section
```

The debris-side guards (`IsDebris && !string.IsNullOrEmpty(DebrisParentRecordingId)`) are applied before calling the shared `ProximitySamplingCadence.Resolve`. See §3.7 for the resolver shape and the wiring pseudocode.

| Distance | Section | Effective `maxSampleInterval` |
|---|---|---|
| `≤ 250 m` | Relative | `configuredMin` (Full) |
| `(250, 500] m` | Relative | `min(configuredMax, 2 × configuredMin)` (Half) |
| `(500, 550] m` (frame hysteresis exit) | Relative | falls through (None) — see note below |
| `> 550 m` | Absolute | falls through to `ResolveDebrisAwareMaxSampleInterval` (None / adaptive) |

The cadence Full→Half boundary at 250 m sits **inside** the Relative entry window (which extends to 500 m). The cadence Half→None boundary at 500 m **coincides with** the Relative section entry boundary. The frame hysteresis-exit zone (500–550 m) is a 50 m transit where the section is still Relative but the cadence has already dropped to None — both `frames` and `bodyFixedFrames` are still being written, just at the existing adaptive rate.

**Hysteresis-exit-zone cadence cost (Low density).** The 500-550 m transit is `None` tier, but `None` still falls through to the legacy debris-aware cap below, so Low density is capped at 0.5 s rather than `configuredMax = 8 s`. That keeps loop-chain error bounded while the section is still Relative. If playtest shows this transit is unnecessarily dense, tune the legacy cap separately; do not bypass it for the hysteresis band.

Tier boundaries have no hysteresis — sample-interval changes are continuous (per-sample selection, no on-disk artifact at boundary crossings).

**Composition with the legacy debris-aware cap (`ResolveDebrisAwareMaxSampleInterval` at `BackgroundRecorder.cs:5269-5275`).** The legacy cap exists today specifically to keep stable-velocity drift at ≤ 0.5 s sample intervals on Medium / Low density once outside the high-fidelity event window. Under v13 the new proximity tier **supersedes** the legacy cap when the tier is non-None, and **falls through to it** when the tier is None. Concretely the wiring at `BackgroundRecorder.cs:1961-1967` becomes:

```csharp
// Re-Fly cadence is FG-only (see :7106 in FlightRecorder); BG passes None.
float effectiveMaxSampleInterval = FlightRecorder.ResolveEffectiveMaxSampleInterval(
    ProximitySamplingTier.None,   // Re-Fly cadence — never non-None for BG-recorded debris
    debrisTier,                    // computed above from ProximitySamplingCadence.Resolve
    highFidelityActive,
    configuredMax,
    configuredMin);

if (debrisTier == ProximitySamplingTier.None)
{
    // Out of proximity (or non-debris recording): apply the legacy
    // adaptive cap to keep stable-velocity drift dense on Medium / Low.
    effectiveMaxSampleInterval = ResolveDebrisAwareMaxSampleInterval(
        effectiveMaxSampleInterval, treeRec);
}
// When debrisTier is Full or Half, the proximity-tier interval is the
// final cap; do not compose with the legacy 0.5 s cap (otherwise Half
// at Low density (interval 1.0 s) would be silently clobbered back to
// 0.5 s by the legacy mid-range cap).
```

The shared `ResolveEffectiveMaxSampleInterval` overload itself:

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

Re-Fly tier wins over debris tier when both non-None (Re-Fly is more specific). For BG-recorded debris, the Re-Fly tier slot is always `ProximitySamplingTier.None` because `ResolveActiveReFlyTreeSamplingCadence` is computed only on the FG path at `FlightRecorder.cs:7106` — BG does not consult Re-Fly state. The 9-cell orthogonality matrix test in §7.1 is defensive against future expansion (e.g., if Re-Fly cadence is ever extended to BG).

Re-Fly cadence has priority over debris cadence because Re-Fly is more specific (the player is actively re-flying); in practice the two are typically disjoint since debris is not the Re-Fly active recording. Tested by the orthogonality matrix in §7.

**BG recorder state extension** in `BackgroundVesselState` (`BackgroundRecorder.cs:202`+; not `:192-200` which is `BackgroundOnRailsState` — on-rails state must NOT carry proximity cadence per the CLAUDE.md "on-rails BG vessels emit no env-classified per-frame TrackSections" invariant):

```csharp
public ProximitySamplingTier debrisProximityTier = ProximitySamplingTier.None;
public double debrisProximityDistanceMeters = double.NaN;
public string debrisProximityReason;
```

**Sample-time wiring** in `OnBackgroundPhysicsFrame` (around `:1958-1967`): compute parent distance, resolve cadence tier via the shared `ProximitySamplingCadence.Resolve` (see §3.7), feed into the extended `ResolveEffectiveMaxSampleInterval`.

**Recorder-side `HighFidelityProximityRangeMeters` raise:** `FlightRecorder.cs:935` from `200.0` → `250.0`, aligning the parent-side density rule (parent's recorder keeps dense samples while split children are within range) with the debris-side full-fidelity tier. Both halves of the proximity pair sample at `configuredMin` simultaneously.

### 3.2 Renderer

Render dispatch by section type:

**Absolute section (`> 500 m`):** standard `IGhostPositioner.InterpolateAndPosition` (`ParsekFlight.cs:16563-16634`) — body-fixed lookup via `body.GetWorldSurfacePosition`. No anchor consulted. Same path solo-flight vessels use.

**Relative section (entered at `≤ 500 m`, retained until `> 550 m`):** body-fixed primary by default. The renderer reads `bodyFixedFrames` (v12 name: `absoluteFrames`) directly via `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow` (rename/docs notwithstanding, this is the existing body-fixed-point path) unless the parent chain contains a loop-anchored ancestor at playback UT. In that loop case, and only that loop case, the renderer invokes the standard `RelativeAnchorResolver` chain:
- Debris's anchor is its parent recording. Read parent's pose at UT t.
- If parent has its own Relative section at UT t → recurse to grandparent.
- If parent is loop-anchored (`LoopAnchorVesselId != 0u`) → resolve parent's pose through the live-anchor path: `liveAnchor.pose(now) × parent.recordedOffset`.
- If parent has Absolute section at UT t and no loop-anchored ancestor exists → do **not** compose; use the debris body-fixed primary surface instead.
- Compose only for loop chains: `debris.world = parent.resolvedPose × debris.recordedOffsetToParent`.

The loop-ancestor detection must be cheap and deterministic: inspect the debris parent chain at the active section UT and return true only if the chain reaches a recording with `LoopAnchorVesselId != 0u` before terminating. Parent missing, parent Absolute, no active parent section, or cycle/guard failure all return false and keep the body-fixed primary path. This helper belongs near the debris playback policy / engine dispatch, not inside `AnchorDetector`; it is a playback surface-selection question, not a recorder anchor-selection question.

**Fallback when loop-chain composition fails** (live anchor missing, callback null, parent chain gap): renderer reads `bodyFixedFrames` body-fixed primary from the Relative section. This fallback should be visible in logs, but it is not a coverage retirement unless both surfaces are missing.

### 3.3 Visibility coupling

Debris in Absolute sections (`> 500 m`) renders independent of parent state. If the parent is destroyed or its recording is deleted after the Absolute section has already been authored, that existing Absolute section continues to render from its body-fixed path.

Debris in Relative sections (entered at `≤ 500 m`, retained until `> 550 m`) also renders independent of parent state by default because body-fixed is primary. It depends on the parent's recorded pose only when the renderer deliberately takes the loop-anchored chain path from §3.2. If that path is unresolvable, the renderer falls back to the body-fixed primary surface (`bodyFixedFrames`) inside the Relative section. The debris remains visible; it just renders at its recording-time world position rather than a loop-chain-resolved position.

The existing `BackgroundRecorder.CheckDebrisTTL` (`:1300-1413`) live-recording-end conditions are preserved unchanged:
1. Parent recording missing from tree (`:1352-1361`).
2. Parent recording closed / superseded (`:1375-1388`).
3. Parent vessel on-rails / destroyed / unloaded (`:1390-1404`).

### 3.4 Loop-anchored ancestor composition (the canonical scenario)

A tanker T is loop-anchored to a station S (`T.LoopAnchorVesselId = S.persistentId`). T sheds a fairing F mid-approach. F is parent-anchored to T (`F.DebrisParentRecordingId = T.RecordingId`); F has no `LoopAnchorVesselId` of its own.

**Recording at separation event:**
- F is close to T (just separated) → first sample is within 500 m → F opens a Relative section anchored on T's recording.
- F's `frames` capture F's anchor-local-to-T offsets at each sample.
- F's `bodyFixedFrames` capture F's body-fixed world coordinates at each sample.

**Recording as F drifts away from T:**
- Inside 250 m of T: Full cadence (configuredMin interval).
- 250–500 m: Half cadence (2 × configuredMin), still in the Relative section. Anchor-local offsets continue to record (the chain still composes for loop scenarios out to 500 m).
- When F crosses 550 m (hysteresis exit), the recorder closes the Relative section and opens an Absolute section. F's `frames` (in the new Absolute section) capture F's body-fixed world coordinates.

**Playback at loop iteration N:**
- The station S is at live pose `Y_N` at the current loop UT.
- T renders via T's loop-anchored Relative path: `T_pose_N = Y_N × T.recordedOffsetToS`.
- For F's Relative section samples (UT t after `≤ 500 m` entry and before `> 550 m` hysteresis exit): the chain resolver walks `F → T`. T's pose at UT t comes from T's loop-anchored resolution, which yields `Y_N × T.recordedOffsetToS_at_t`. F's pose = `T_pose_N_at_t × F.recordedOffsetToT_at_t`. F follows T which follows S. **Correct across loop iterations.** Chain math is dense in 0–250 m (Full cadence), half-rate in 250–500 m (Half cadence), and legacy-capped in the 500–550 m hysteresis transit.
- For F's Absolute section samples (UT t past the 550 m hysteresis exit): F renders at its body-fixed world coordinate. At loop iteration N with a long loop period, this can be far from station S; but F at that UT was *already* far from T at recording time, and visual coherence with the loop anchor is weak by that distance anyway. Accepted limitation.

**Resolver gate relaxation AND live-anchor callback.** Currently `RelativeAnchorResolver.cs:301-310` rejects loop-anchored recordings as anchor targets with `loop-anchor-out-of-scope`. The previous draft of this plan claimed "the chain composes through the loop-anchored ancestor's own live-anchor path" — but no such path exists *inside* the resolver. Live-PID resolution lives in `ParsekFlight` parallel to the resolver, not inside it. The carve-out by itself is necessary but not sufficient: after the rejection is relaxed, the resolver continues walking T's TrackSections and either finds an Absolute section (returns T's recording-time body-fixed pose, losing the loop) or finds a Relative section anchored on a live-PID `anchorVesselId` (fails to compose because S is a live vessel, not a recording in the DAG).

Under v13 we need **two coordinated changes**:

**(a) Carve-out predicate** at the existing rejection site `RelativeAnchorResolver.cs:301-310` — debris-focus is allowed to chain through loop-anchored ancestors:

```csharp
if (anchorRecording.LoopAnchorVesselId != 0u)
{
    bool focusIsDebris = TryGetFocusRecording(context, out Recording focus)
        && focus != null
        && focus.IsDebris;
    if (!focusIsDebris)
    {
        return /* existing loop-anchor-out-of-scope rejection */;
    }
    // Allow chain walk to continue into T's TrackSections.
}
```

(Note: the `:450-459` site is inside the `WithReliability` parallel resolver tree being deleted whole, see §6.2. After deletion there is only the one rejection site to relax.) `TryGetFocusRecording` reads `context.FocusRecordingId` and looks it up in `context.FocusTree.Recordings`.

The "focus is debris" predicate handles cascade correctly: in `T → F → G` (G is debris-of-debris, F is debris, T is loop-anchored), focus G is debris all the way down, so the carve-out fires uniformly on every chain hop. The narrower "immediate parent" predicate (`anchoredRecording.DebrisParentRecordingId == anchorRecording.RecordingId`) would fail in cascade because G's parent is F (not T).

**(b) Live-anchor callback on `RelativeAnchorResolverContext`.** Once the carve-out fires past `:301-310` and the resolver recurses into T's TrackSections, the chain walk hits `TryResolveRelativeSectionPose` (`RelativeAnchorResolver.cs:1926-2018`) for T's own Relative section. That function calls `TryResolveSectionAnchorRecordingId` to read the section's `anchorRecordingId` (string). T's Relative section against the live station S has `anchorRecordingId == null` and `anchorVesselId == S.persistentId` (the legacy loop layout — see `FlightRecorder.cs:5039-5052`), so the helper returns false and the function emits `legacy-anchor-recording-id-missing` at `:1945-1955`. **This is the exact hook point** for the live-PID compose.

**Context struct extension** (`RelativeAnchorResolver.cs:35-92` — the context is `internal readonly struct` with `public readonly` fields, a parameterized constructor at `:49-75`):

The callback signature must mirror `TryResolveLoopLiveAnchorPose` (`ParsekFlight.cs:21062-21066`): `bool TryResolveLoopLiveAnchorPose(uint anchorVesselId, string victimRecordingId, double targetUT, out RelativeAnchorPose pose)`. The host needs the `victimRecordingId` for diagnostic-trace context; pass it through the callback signature.

The existing `DebrisLocalOffsetSquaredMeters` field (`:47`), its constructor parameter (`:60, :74`), and the `WithDebrisLocalOffsetSquaredMeters` clone helper (`:77-91`) are **orphaned by the `WithReliability` tree deletion in §4.2** — the only consumers (`:1910, :2095, :2106, :2116`) live inside the deleted tree. Delete the field, the constructor parameter, and the clone helper as part of this commit. The new `TryResolveLiveAnchorTransform` parameter takes the old `debrisLocalOffsetSquaredMeters` parameter's slot in the constructor signature.

```csharp
// Add to RelativeAnchorResolverContext struct (REPLACING the deleted
// DebrisLocalOffsetSquaredMeters field at :47):
public readonly Func<uint, string, double, (Vector3d pos, Quaternion rot)?> TryResolveLiveAnchorTransform;
// ^ (anchorVesselId, victimRecordingId, ut) → (worldPos, worldRotation) or null

// Constructor at :49-75 — drop `debrisLocalOffsetSquaredMeters` parameter
// (and the line at :74 assigning it), add the callback parameter in its place:
public RelativeAnchorResolverContext(
    // ... existing parameters EXCEPT debrisLocalOffsetSquaredMeters ...
    Func<uint, string, double, (Vector3d pos, Quaternion rot)?> tryResolveLiveAnchorTransform = null)
{
    // ... existing assignments EXCEPT DebrisLocalOffsetSquaredMeters ...
    TryResolveLiveAnchorTransform = tryResolveLiveAnchorTransform;
}

// WithDebrisLocalOffsetSquaredMeters clone helper at :77-91 is DELETED whole
// — no caller after WithReliability tree deletion.
```

**Hook inside `TryResolveRelativeSectionPose`** at the existing `legacy-anchor-recording-id-missing` leaf:

```csharp
// RelativeAnchorResolver.cs:1938-1956, after TryResolveSectionAnchorRecordingId returns false:
if (!TryResolveSectionAnchorRecordingId(context, recording, section, sectionIndex, out string anchorRecordingId))
{
    // NEW v13 live-anchor compose: when the section carries a live-PID
    // anchor (loop-anchored layout) AND the carve-out path is active
    // (focus is debris, callback populated), compose the parent pose
    // against the live anchor instead of emitting the legacy failure.
    if (section.anchorVesselId != 0u
        && context.TryResolveLiveAnchorTransform != null
        && TryGetFocusRecording(context, out Recording focus)
        && focus != null
        && focus.IsDebris)
    {
        var liveAnchor = context.TryResolveLiveAnchorTransform(
            section.anchorVesselId, recording.RecordingId, ut);
        if (liveAnchor.HasValue)
        {
            // T's pose at ut = live anchor pose × T's recorded offset to S at ut
            // (use the same anchor-local → world composition the existing
            // resolver applies after a successful recording-id lookup; the
            // only difference is the anchor pose comes from the callback
            // instead of from a recursive TryResolveAnchorPose call).
            pose = ComposeAnchorLocalToWorld(
                liveAnchor.Value.pos,
                liveAnchor.Value.rot,
                section,
                sectionIndex,
                ut);
            return true;
        }
    }

    // Existing v12 failure path (now narrower in scope — only fires when
    // the live-anchor callback isn't populated or returns null):
    string reason = recording.RecordingFormatVersion >= RecordingAnchorChainFormatVersion
        ? "anchor-recording-id-missing"
        : "legacy-anchor-recording-id-missing";
    failure = WarnUnresolved(...);
    return false;
}
```

`ComposeAnchorLocalToWorld` is a small new helper extracted from the existing post-anchor-resolved composition logic at `:1958-2018` (the resolver already composes anchor-pose × anchor-local-offset → world pose after a successful recording-id lookup; factor that out so the new live-PID path uses identical math, modulo where the anchor pose comes from).

**Live-anchor pid resolution at the hook.** The live anchor pid is resolved as `section.anchorVesselId != 0u ? section.anchorVesselId : recording.LoopAnchorVesselId`. The recording-level fallback covers loop-anchored sections whose `anchorVesselId` field was written as zero (the recorder may emit zero when the section is implicitly anchored against the recording's declared loop anchor). Engine-side, `GhostPlaybackEngine.ShouldUseLoopAnchoredDebrisChain` mirrors this by accepting either `section.anchorVesselId == recording.LoopAnchorVesselId` or `section.anchorVesselId == 0u`. A non-zero mismatched section pid (mid-loop anchor flip) is rejected at the engine gate so playback falls to body-fixed primary instead of chasing a non-loop live PID across loop iterations.

**Why this specific hook point.** The reviewer's check confirmed: `TryResolveRecordingPose` (`:280-427`) dispatches by `section.referenceFrame` and calls `TryResolveRelativeSectionPose`. Relative-section anchor resolution does NOT consult `section.anchorVesselId` in the chain-walking path — only the leaf failure case does. Under v13 (which rejects v12 and below at load), debris→parent links are uniformly stored in `anchorRecordingId` (string), so the predicate `section.anchorVesselId != 0u` essentially fires only at the loop-anchor leaf (T's section against live S). That's exactly the place the new compose belongs.

**Carve-out at `:301-310` is still required.** Without the carve-out relaxation, the resolver bails out at T's recording-level rejection (before walking T's TrackSections). The carve-out allows recursion to reach `TryResolveRelativeSectionPose` on T's section; the new hook above is what actually returns a live-anchor-resolved pose. Both pieces are necessary.

**New `TryGetFocusRecording` helper** (referenced by both the `:301-310` carve-out predicate and the `:1945` hook above). Resolution order:

```csharp
private static bool TryGetFocusRecording(
    RelativeAnchorResolverContext context, out Recording focus)
{
    focus = null;
    if (string.IsNullOrEmpty(context.FocusRecordingId)) return false;

    // Prefer ProvisionalRecordings (in-flight overlay) over committed tree.
    // PendingTree is consulted last; it carries pre-merge transient state.
    if (context.ProvisionalRecordings != null
        && context.ProvisionalRecordings.TryGetValue(context.FocusRecordingId, out focus)
        && focus != null)
        return true;
    if (context.FocusTree?.Recordings != null
        && context.FocusTree.Recordings.TryGetValue(context.FocusRecordingId, out focus)
        && focus != null)
        return true;
    if (context.PendingTree?.Recordings != null
        && context.PendingTree.Recordings.TryGetValue(context.FocusRecordingId, out focus)
        && focus != null)
        return true;

    focus = null;
    return false;
}
```

This contract matches the existing `RelativeAnchorResolver` precedent for resolving anchor recording ids (which also walks Provisional → Focus → Pending). Null `FocusRecordingId` returns false → carve-out doesn't fire → existing rejection holds.

**Host wiring decision.** Keep `BuildFlightRelativeAnchorResolverContext` (`ParsekFlight.cs:15478-15492`) **static**, but populate the live-anchor callback via a static accessor `ParsekFlight.TryGetLiveAnchorTransformDelegate()` that reads `ParsekFlight.Instance` (singleton at `:174`) and returns the bound delegate. Both construction sites — `BuildFlightRelativeAnchorResolverContext` AND `ProductionAnchorWorldFrameResolver.TryBuildRelativeAnchorResolverContext` (`:471`) — call the accessor.

```csharp
// New on ParsekFlight (static, public-internal-or-internal):
internal static Func<uint, string, double, (Vector3d pos, Quaternion rot)?>
    TryGetLiveAnchorTransformDelegate()
{
    ParsekFlight host = Instance;
    if (host == null) return null;
    return (vesselPid, victimRecordingId, ut) =>
    {
        if (!host.TryResolveLoopLiveAnchorPose(vesselPid, victimRecordingId, ut,
                out RelativeAnchorPose pose))
            return null;
        return (pose.worldPos, pose.worldRotation);
    };
}

// Inside BuildFlightRelativeAnchorResolverContext (static, unchanged signature):
return new RelativeAnchorResolverContext(
    /* ... existing args ... */,
    tryResolveLiveAnchorTransform: TryGetLiveAnchorTransformDelegate());

// Inside ProductionAnchorWorldFrameResolver.TryBuildRelativeAnchorResolverContext:
context = new RelativeAnchorResolverContext(
    /* ... existing args ... */,
    tryResolveLiveAnchorTransform: ParsekFlight.TryGetLiveAnchorTransformDelegate());
```

Single static accessor. No instance-method cascade through `TryBuildRelativeAnchorResolverContext` (`:22794`) and its callers (`:17943, :18098`). The four flight-scene callers of `BuildFlightRelativeAnchorResolverContext` (`:15170, :15399, :22717, :22802`) are untouched. When `Instance` is null (recorder-side, headless tests), the accessor returns null and the context's callback stays null — the existing rejection still fires correctly at the resolver's leaf.

`TryResolveLoopLiveAnchorPose` (`ParsekFlight.cs:21062-21066`) is instance-scoped and reads instance fields. The lambda captures `host` (the `Instance` value at delegate-construction time) so the instance reference is bound at the point the delegate is built, not at every invocation.

`TryResolveLoopLiveAnchorPose` returns a private `ParsekFlight.RelativeAnchorPose` struct (`:307-310`); the callback signature uses `(Vector3d pos, Quaternion rot)?`. The lambda extracts `pose.worldPos` and `pose.worldRotation` (struct field names — NOT `worldPosition`). See the `TryGetLiveAnchorTransformDelegate` snippet above.

**All `RelativeAnchorResolverContext` construction sites** (verified by `grep -rn "new RelativeAnchorResolverContext"` — six production sites plus three test sites; plus the `WithDebrisLocalOffsetSquaredMeters` clone helper itself):

| Site | Disposition |
|---|---|
| `ParsekFlight.cs:15483` (inside `BuildFlightRelativeAnchorResolverContext`, static) | **Populate callback via `ParsekFlight.TryGetLiveAnchorTransformDelegate()`** static accessor. Static-on-static — no instance-method conversion needed. |
| `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:471` (`TryBuildRelativeAnchorResolverContext`) | **Populate callback via the same static accessor.** Same call as the `BuildFlightRelativeAnchorResolverContext` site. Singleton lookup; safe at headless test time (returns null, which the resolver handles). |
| `BackgroundRecorder.cs:5019` | **Leave callback null.** Recorder-side context never walks chains across the live-anchor boundary; uses parent's live pose directly via `ResolveDebrisParentDistanceMeters`. Null callback is correct. |
| `FlightRecorder.cs:7637` | **Leave callback null.** Same rationale — recorder-side. |
| `RecordedRelativeAnchorPoseResolver.cs:106` | **Leave callback null.** Map / KSC-scene resolver path; loop playback is flight-scene-only. Document in the source comment. |
| `RelativeAnchorResolver.cs:79` (`WithDebrisLocalOffsetSquaredMeters` clone helper) | **DELETE this helper** — see §4.2 (the field it propagates, `DebrisLocalOffsetSquaredMeters`, is orphaned by the WithReliability tree deletion). Without the clone helper, the new callback doesn't need a propagation line. |
| `Source/Parsek.Tests/RelativeAnchorResolverTests.cs:1959` (and the `MakeContext` helper at `:1952`) | **Update `MakeContext`** to accept an optional `focusRecordingId` parameter (currently hardcoded `null`); new carve-out tests need a non-null `focusRecordingId` so `TryGetFocusRecording` returns the debris focus. Pass `null` callback by default in non-callback tests; tests that exercise the live-anchor path pass a deterministic stub. |
| `Source/Parsek.Tests/Rendering/ProductionAnchorWorldFrameResolverTests.cs:247` | Test-side construction. Pass `null` callback unless the test specifically exercises live-anchor composition. |
| `Source/Parsek.Tests/Harness/HarnessScenarios.cs:35` | Test-side construction. Pass `null` callback. |

**Recorder-side `FocusRecordingId == null` handling.** `BackgroundRecorder.cs:5021` constructs the context with `focusRecordingId: null`. With focus null, `TryGetFocusRecording` returns false, and the carve-out predicate `focus.IsDebris` is false — the existing rejection fires unchanged. The recorder doesn't need the carve-out because it uses live parent pose directly (the chain math isn't on the recording path). Documented here so a reader doesn't expect the carve-out to fire universally.


**Why this design choice over alternatives:**

- **Engine-path dispatch** (route debris-on-loop-anchored chains through a separate engine path): doubles the dispatch logic. Resolver-internal hook is narrower and keeps the §3.2 "dispatch by section type" promise.
- **Body-fixed-shadow only** (drop the chain claim): the body-fixed shadow stores recording-time positions; at loop iteration N the station has moved but the shadow renders debris at the original position. The very failure mode this plan was created to fix.

**Non-debris focus safety:** the carve-out predicate is false for non-debris focus; the existing rejection fires unchanged. `scripts/grep-audit-non-loop-live-pid.ps1` continues to gate the wider invariant.

**Cascade:** T → F (debris) → G (debris-of-debris). G is parent-anchored to F. The chain G → F → T → live-anchor-of-T (S) composes naturally at each level. G doesn't need any special handling; whatever loop semantics exist at T propagate transparently through F to G.

**Production cascade depth is hardcoded to 1** (`BackgroundRecorder.cs:99: internal const int MaxRecordingGeneration = 1`), so T → F → G never occurs in real recordings — debris-of-debris is silently dropped at the recorder cap. The cascade-correctness reasoning above is forward-looking; the §7.2 cascade test (`RelativeAnchorResolver_CascadeDebris_AnchoredOnLoopAnchoredGrandparent_Allowed`) uses a synthetic fixture that bypasses the cap. If `MaxRecordingGeneration` is ever raised, the chain math is already correct for it.

**No `LoopAnchorVesselId` inheritance on debris.** The earlier draft proposed inheriting the field from parent at split; this redraft drops that mechanism. Loop semantics flow through the chain, not through field inheritance. Simpler, fewer state propagations, naturally handles cascades.

### 3.5 Cascade separations (non-loop case)

Standard chain: when debris piece A sheds debris piece B, B's `DebrisParentRecordingId` is set to A's `RecordingId` (the immediate parent, regardless of whether A is itself debris). At rendering, the chain B → A → A's parent → ... composes through whatever the chain's root looks like.

`MaxRecordingGeneration` at `BackgroundRecorder.cs:1629` caps cascade depth (existing rule). Beyond the cap, further debris-of-debris is silently dropped.

### 3.6 Renaming: `absoluteFrames` → `bodyFixedFrames`; "absolute is primary, relative is shadow"

The v12 schema named the body-fixed parallel list `TrackSection.absoluteFrames` and described it pervasively as the *shadow* (e.g. `TrackSection.cs:59` field comment "*planet-relative shadow payload*"; `DebrisRelativePlaybackPolicy.cs:50` *"v7 absolute shadow is not [the primary]..."*; ~20+ comment/log/XML-doc sites across `FlightRecorder.cs`, `BackgroundRecorder.cs`, `ParsekFlight.cs`, `GhostPlaybackEngine.cs`, `GhostMapPresence.cs`, etc.). Under v13 the architectural roles are inverted: body-fixed is the **primary** rendering surface (always available, used for default render), anchor-local-to-parent in `frames` is the **secondary** "shadow" surface (used only for chain composition through loop-anchored ancestors, see §3.4).

The plan **renames the field and rebrands every comment / log / doc** to match v13 semantics. This is a mechanical change but touches every site listed below; v13 will not feel right to a future reader if the field is named `absoluteFrames` and the comments still call it "the shadow."

**Field rename:**

```csharp
// TrackSection.cs:59 — before:
public List<TrajectoryPoint> absoluteFrames; // For Relative only: planet-relative shadow payload

// after:
public List<TrajectoryPoint> bodyFixedFrames; // For Relative only: body-fixed world-coordinate primary surface (the v13 primary inside Relative sections; v12 named this "absolute shadow")
```

Field name (`absoluteFrames` → `bodyFixedFrames`) is content-based and neutral. The v12 "shadow" wording was role-based and is now inverted.

**Comment / log / doc rebranding rule:** any text using "absolute shadow" / "shadow track" / "shadow payload" / "shadow point" / "shadow lerp" in a context referring to `absoluteFrames` (now `bodyFixedFrames`) gets rewritten:

- v12 phrasing: *"falls back to absolute shadow"* → v13 phrasing: *"falls back to body-fixed primary inside the Relative section"*
- v12 phrasing: *"v7 absolute shadow is not a primary substitute"* → v13 phrasing: *"under v13 body-fixed is the primary; the anchor-local `frames` is the secondary surface used only for chain composition through loop-anchored ancestors"*

The v12 role of "absoluteFrames as shadow" used the field as a defensive fallback when the resolver couldn't resolve the anchor. Under v13 the field is consulted **first** for default render (so the renderer no longer needs to walk the chain for non-loop debris); the resolver chain is only invoked when the chain composes through a loop-anchored ancestor (§3.4).

**Sites to update** (non-exhaustive — implementer uses `grep -rn "absoluteFrames\|absolute shadow" Source/Parsek/` to find every site):

| File | Lines | Site type |
|---|---|---|
| `Source/Parsek/TrackSection.cs` | `:46, :59, :80` | Field declaration + struct doc + ToString format |
| `Source/Parsek/FlightRecorder.cs` | `:4881, :6837-6841, :6852, :6860, :6917, :6940, :8200-8205, :8267-8269, :8361-8373, :8909` | Field writes + variable names (`absoluteShadowPoint` → `bodyFixedPoint`) + log lines + comments |
| `Source/Parsek/AnchorDetector.cs` | `:227` | Comment about playback fallback |
| `Source/Parsek/DebrisRelativeCoveragePrimitives.cs` | `:59` | Comment about coverage |
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | `:50` and other v12-shadow-as-non-primary doc | Comment / doc rewrite |
| `Source/Parsek/GhostMapPresence.cs` | `:5162` | Comment about callers |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2787, :2833, :5916, :5984` | Comments + log strings (`"relative absolute shadow"` → `"body-fixed primary"`) |
| `Source/Parsek/ParsekFlight.cs` | `:15141, :16687, :18222, :21193, :22115, :22178, :22268, :22850` | XML docs + comments + log strings |
| `Source/Parsek/IGhostPositioner.cs` | `:65-77` (entire XML doc block above `TryPositionFromRelativeAbsoluteShadow`) | Doc block rewrite — heavily entangled with deleted concepts (tumbling-parent gate, Phase D, `AnchorRotationUnreliableRoute` cross-ref). Method signature at `:78-80` stays; the method itself is retained as the chain-failure fallback per §4.1. Aligns with the §6.2 row citing `:65-77`. |
| `Source/Parsek/SessionMerger.cs` | `:803, :814, :836, :848, :1197, :1212, :1214, :1216, :1221, :1439, :1440` | 10 `absoluteFrames` identifier sites (local variables, field reads inside merge logic) **plus the source-tag log strings** `"absoluteFrames.match-last"` / `"absoluteFrames.match-first"` at `:1221` — rename source-tag string content too (these are consumed by post-merge log-grep tooling). The merge logic operates per-section and is correct under v13 (§6.4 audit row stays — semantics unchanged). |
| `Source/Parsek/TrajectoryTextSidecarCodec.cs` | `:644, :1167, :1635-1643, :1785-1810, :1809-1810` | Local variable + field references + on-disk node name `ABSOLUTE_POINT` → `BODY_FIXED_POINT`. See on-disk implications block below. |
| `Source/Parsek/TrajectorySidecarBinary.cs` | `:386, :388, :389, :756, :785, :791` | C# field-read expressions `track.absoluteFrames` → `track.bodyFixedFrames`. The wire format is unchanged (positional read/write) but the C# source still needs the rename. Easy to miss because the "binary codec is unchanged" intuition obscures the C# source-level dependency. |
| `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` | `:731, :951` | Log strings that reference `"TrackSection[i].absoluteFrames"` by name (consumed by `SceneExitFinalizationIntegrationTests.cs:183, :1535`). Rename the string content; update the test-side assertion strings to match. |
| Test files in `Source/Parsek.Tests/` | various | xUnit assertions, fixture builder names, log assertion strings |

Implementer should grep-verify completeness: `grep -rn "absoluteFrames\|absolute shadow\|ABSOLUTE_POINT" Source/Parsek/ Source/Parsek.Tests/` returns zero hits post-rename (excluding the historical-mention sites in `docs/dev/` plus this plan doc).

**On-disk implications:**

- **Binary `.prec` codec** (`TrajectorySidecarBinary.cs`) writes / reads by position. The on-disk wire format is unchanged. **But the C# expressions still rename**: every `track.absoluteFrames` field reference in the codec source becomes `track.bodyFixedFrames`. Sites: `:386, :388, :389, :756, :785, :791` (six read/write expressions). The "no change" claim refers to the wire format only, not the C# source — be explicit so the implementer doesn't skip these sites during the rename pass.
- **ConfigNode codec** (`TrajectoryTextSidecarCodec.cs:644, :1635-1643, :1785-1810`) writes per-Relative-section `ABSOLUTE_POINT` child nodes today. Under v13 rename the on-disk node name to **`BODY_FIXED_POINT`** at the write site (`:1635-1643`) and the read site (`:1785-1810`). v12-and-earlier saves are rejected at load (§5.2 / §5.3) so backward-compat reading of `ABSOLUTE_POINT` is not required. Local variables / field reads at `:644, :1167` also rename to match.
- **v7 parallel-list invariant** ("populated only when section is Relative") is unchanged; it now applies to the renamed field.

The §7.3 `V13Recording_RoundTrip` test asserts the renamed key round-trips byte-identical for both codecs.

**What this rename does NOT change:**

- v12 binary `.prec` reader path stays intact for one release (codec layer; load gate rejects above). Not strictly needed since v12 is rejected, but harmless dead code that can be cleaned up in a follow-up.
- The semantics of the parallel-list invariant ("populated only when section is Relative").

**What the rename DOES enable:**

- A future reader of the renderer's default path sees `traj.bodyFixedFrames` (in a Relative section) or `traj.frames` (in an Absolute section) and immediately knows that *body-fixed is the surface being rendered*. The v12 "shadow" mental model is gone.
- Future v13 documentation / tests can speak of "the relative-to-parent shadow" inside Relative sections and mean `frames` (the anchor-local data, used only for chain composition). Naming is now content-correct, not role-historical.

This rename is a separate logical commit in §8 rollout — sequenced AFTER the renderer cleanup (commit 2) and BEFORE the recorder change (commit 3) so that the renamed field is in place before the new recorder writes start producing v13 sections.

### 3.7 Shared proximity-cadence module

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

- **Re-Fly** (`FlightRecorder.ResolveActiveReFlyTreeSamplingCadence` at `:752-819`): keep the Re-Fly-specific guards (marker null/empty checks, session-id check, tree-id check, ut-finite check), then delegate the tier resolution to `ProximitySamplingCadence.Resolve(proximityDistanceMeters, ReFlyTreeFullFidelityProximityRangeMeters, ReFlyTreeHalfFidelityProximityRangeMeters, out reason)`. The `ResolveReFlyTreeCadenceSampleInterval` helper at `:867-876` becomes a one-line forward to `ProximitySamplingCadence.ResolveSampleInterval` — **but watch the parameter order**: the existing `ResolveReFlyTreeCadenceSampleInterval` takes `(cadence, configuredMax, configuredMin)` (max then min); the shared module's `ResolveSampleInterval` takes `(tier, configuredMin, configuredMax)` (min then max). The forwarder must re-order its arguments. Easy to mis-wire on a "one-line forward."
- **Debris** (new helper per §3.1): apply debris-specific guards (`IsDebris && DebrisParentRecordingId != null`), then delegate to `ProximitySamplingCadence.Resolve(parentDistanceMeters, DebrisFullFidelityProximityRangeMeters, DebrisHalfFidelityProximityRangeMeters, out reason)`.
- **Future recorders** (e.g. a co-bubble formation-flying recorder, an approach-corridor recorder) reuse the same module with their own thresholds and their own guards.

**Behaviour-preserving refactor for Re-Fly — with one explicit caveat: reason-string mapping.** Same thresholds (250 m full, 500 m half), same tier semantics (None / Half / Full), same composition with `ResolveEffectiveMaxSampleInterval`. The existing `ResolveActiveReFlyTreeSamplingCadence` (`FlightRecorder.cs:760-818`) returns Re-Fly-specific reason strings (`"active-refly-tree-full"`, `"active-refly-tree-half"`, `"proximity-out-of-range"`, `"proximity-missing"`, etc.); the shared module returns generic strings (`"full"`, `"half"`, `"out-of-range"`, `"distance-missing"`). Any log-grep tooling keyed off the Re-Fly-specific strings would break if we change the wire format.

The Re-Fly guard wrapper post-translates the shared-module reason to the Re-Fly-specific string before returning. Mapping:

| Shared-module reason | Re-Fly wrapper translates to |
|---|---|
| `"full"` | `"active-refly-tree-full"` |
| `"half"` | `"active-refly-tree-half"` |
| `"out-of-range"` | `"proximity-out-of-range"` |
| `"distance-missing"` | `"proximity-missing"` |

The Re-Fly-specific early-exit reasons (`"active-recording-id-missing"`, `"active-tree-id-missing"`, `"marker-missing"`, `"marker-session-missing"`, `"marker-tree-missing"`, `"marker-active-recording-missing"`, `"ut-non-finite"`, `"tree-mismatch"`) all fire **before** the shared-module call inside the Re-Fly guard chain, so they pass through unchanged.

**Guard ordering must match the existing implementation.** The existing `ResolveActiveReFlyTreeSamplingCadence` (`FlightRecorder.cs:752-819`) emits its early-exit reasons in a specific order, and `proximity-missing` (`!IsFinite(proximityDistanceMeters)`) fires AFTER `tree-mismatch` but BEFORE the distance buckets. The Re-Fly wrapper must preserve this exact order so existing tests in `AdaptiveSamplingTests.cs` (the 5 cases there) continue to pass byte-identically. The §7.1 regression test `ReFlyResolver_RefactorEqualsOldBehaviour` covers any drift across the full input matrix.

Regression test (§7.1): assert byte-identical reason strings between the pre-refactor `ResolveActiveReFlyTreeSamplingCadence` and the post-refactor wrapper across the full input matrix (all early-exit branches + the three tier outcomes).

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
    state.debrisProximityTier = debrisCadence;
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
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | Coverage gating still applies for Relative debris sections (entered at 500 m, retained until 550 m). Update `ShouldRetireFromDiagnostic` (`:191-199`) to NOT fire on `non-relative-section` (otherwise it would retire debris in the new Absolute sections). |
| `DebrisRelativePlaybackPolicy.TryResolveInitialStructuralSeedBridgeEndUT` (`:232-293`) and its three call sites at `GhostPlaybackEngine.cs:5562, :5652, :5668` | **Init-hide mechanism, explicitly preserved.** At debris activation, the recorder writes a structural seed point at the separation origin followed by the first ordinary sample several frames later. Without this helper, the ghost would render a visible slide from the seed pose to the first interpolated pose. The helper detects the seed-to-first-sample bridge and keeps the ghost mesh hidden until the first ordinary sample lands. Under v13 this fires for the typical case (debris born within 500 m of parent → opens Relative section → seed-bridge applies). For the rare debris-born-beyond-500m case (opens Absolute), no bridge is needed because the section is body-fixed from the seed onward. **Do not touch this code path during v13 cleanup**; it is independent of the deleted shadow-route / tumble-gate machinery. Add a §7.4 in-game regression test confirming no visible slide at separation. |
| `Source/Parsek/DebrisRelativeRecorderPolicy.cs` | Tail normalization for Relative sections still relevant inside the entry/hysteresis window (enter at 500 m, retain until 550 m). Audit each method (six call sites in `BackgroundRecorder` at `:1486, :2198, :2431, :2605, :4269, :6010` plus three in `RecordingFinalizationCacheApplier` at `:119, :162, :169`) for the v12 all-Relative assumption — they should iterate sections by type and treat Absolute debris sections as renderable surfaces. Rename `ShouldNormalizeParentAnchoredDebris` → `ShouldNormalizeDebrisRelativeTail` to reflect the narrower scope. |
| `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow` (interface + impl at `ParsekFlight.cs:16717-16810`) | Used as the body-fixed primary path for non-loop Relative debris and as the fallback when loop-chain resolution fails (§3.2). The method name is legacy; update docs/log text but keep the signature for scope control. |
| `BackgroundRecorder.UpdateBackgroundAnchorDetection` debris early-return (`:4295-4303`) and `ApplyDebrisAnchorContractToState` (`:4600-4685`) | Repurposed: now fires when entering at `≤ 500 m` or retaining an already-open Relative section through `≤ 550 m`. The unconditional "always Relative" shortcut is gated on the stateful proximity/hysteresis check from §3.1. The third call site at `:4727` (structural-event seam in `ApplyBackgroundRelativeOffset`) uses the same stateful gate. |
| `Recording.ApplyDebrisAnchorContract` and `Recording.DebrisParentRecordingId` | Still set at split time. Still consumed for identification (tree hierarchy, ledger, future UI). |

### 4.2 What gets deleted

| File / symbol | Why it goes |
|---|---|
| `Source/Parsek/LegacyDebrisShadowGate.cs` (entire file) | v11 retroactive fix; v11 recordings are no longer loadable under v13. |
| `Source/Parsek/TumblingParentInterpolationGate.cs` (entire file) | Only consumer was `ShouldEvaluateAnchorRotationReliability`. Under v13 non-loop Relative debris renders from body-fixed primary, so parent tumble no longer drives position. Loop-anchored Relative debris can still use chain math inside 500 m, but that is an explicit loop-correctness trade-off covered by cadence and body-fixed fallback. If playtest reveals visible artifacts in loop-chain debris, a future renderer-time fallback can be reintroduced narrowly for loop chains. |
| `Source/Parsek/GhostPlaybackEngine.cs` `TryRouteAnchorRotationUnreliable` (`:2796-2909`), `TryRouteAnchorRotationToShadow` (`:2928-3006`), `AnchorRotationUnreliableRoute` enum | Router for the PR #803 always-shadow / tumble-gate path. No longer needed: v13 has a simpler debris surface selector (§3.2): body-fixed primary by default, Relative resolver only for loop-anchored ancestor composition, body-fixed primary fallback on loop-chain failure. |
| `Source/Parsek/ParsekFlight.cs` `ShouldEvaluateAnchorRotationReliability` (`:15131-15150`), `ShouldEvaluateAnchorRotationReliabilityForTesting` (`:15128-15129`), `TryEvaluateAnchorRotationReliability` (`:15152`+) | Tumble-gate predicate; orphaned by the router deletion. |
| `Source/Parsek/ParsekFlight.cs` `TryUseLegacyDebrisShadowFallback` (`:22857-22905`), `TryRetireParentAnchoredDebrisOnRecordedAnchorMiss` (`:22907-22933`) | Legacy-v11-specific helpers. |
| `Source/Parsek/ParsekFlight.cs` call sites at `:22122, :22181, :22275` | `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` branches. |
| `Source/Parsek/ParsekFlight.cs:22947-22961` | Debris-only guard inside `TryUseRelativeAbsoluteShadowFallback`. The wrapper itself stays (used by non-debris Relative recovery); just the inner debris-specific check goes. |
| `Source/Parsek/GhostPlaybackState.cs` fields `anchorRotationShadowRoutedThisFrame` (`:93`) and `parentAnchoredDebrisCoverageRetired` (`:99`) + reset in `ResetGhostState` (`:152-153`) | Both fields fed the deleted tumble-gate FX-suppression flow. |
| `Source/Parsek/GhostPlaybackEngine.cs` all read/write sites of `anchorRotationShadowRoutedThisFrame` and `parentAnchoredDebrisCoverageRetired` | Delete every reference (sample line numbers `:1148, :1249, :1256, :1418, :1661, :1847, :1861, :2124, :2134, :2223, :2320, :2331, :2380, :2977, :2983, :3031, :3136, :3163, :3223, :5203, :5242` are informative-only; the authoritative cleanup is via the §6.6 grep audit — both symbols should return zero hits post-cleanup). |
| `Source/Parsek/GhostPlaybackEvents.cs:89, :102` XML-doc references inside the deleted enum/doc block | Same. Delete with `AnchorRotationUnreliableRoute`; the actual `shadowRouted` helper parameter sites are in `GhostPlaybackEngine.cs` and are listed below. |
| `Source/Parsek/ParsekFlight.cs:16701` (XML doc reference to `anchorRotationShadowRoutedThisFrame`) | Same. |
| `Source/Parsek/IGhostPositioner.cs:78-80` (`TryPositionFromRelativeAbsoluteShadow` interface signature) — KEEP for the §3.2 body-fixed primary path | (See §4.1 above; this is in the "stays" list. Listed here only to flag the implementation-time confusion risk: the symbol is named after the deleted always-shadow path but its remaining role is body-fixed primary/fallback.) |
| `Source/Parsek/BackgroundRecorder.cs:1868-1878` (Re-Fly settle suppression for debris) and `ShouldSuppressParentDebrisForReFlySettle` (`:2046-2053`) | Suppression rationale ("Relative section against a suppressed parent would leave the resolver unable to walk back to a parent pose") is gone — under v13, the body-fixed shadow is on every Relative debris sample, so resolver failure during settle falls through to shadow gracefully. The suppression now introduces unwanted sample gaps with no benefit. |
| `RelativeAnchorResolver.cs` `WithReliability` parallel resolver tree (~9 methods, ~500 lines) and its public entry `TryEvaluateRecordingAnchorRotationReliability` at `:264` | The entire `WithReliability` tree (`TryResolveAnchorPoseWithReliability`, `TryResolveRecordingPoseWithReliability`, `TryResolveRelativeSectionPoseWithReliability`, `TryResolveAbsoluteSectionPoseWithReliability`, `TryResolveSmallSectionGapPoseWithReliability`, `TryResolveSameChainContinuationPoseWithReliability`, `TryResolveTerminalClampedPoseWithReliability`, `TryResolveAbsoluteBracketPoseWithReliability`, `TryResolveAbsoluteFramesPoseWithReliability`) exists only to plumb `AnchorRotationReliabilityDecision` to the deleted tumble gate. With the gate gone and its single production caller (`ParsekFlight.TryEvaluateAnchorRotationReliability:15180`) deleted, the entire parallel tree is dead code. Delete in full; do NOT just "trim 12 signatures." The `TumblingParentInterpolationGate.EvaluateParentRotationInterpolation` call at `:2102` and the `MinOffsetMagnitudeSquaredMeters` reference at `:2096` go with their containing method. |
| `Source/Parsek.Tests/RelativeAnchorResolverTests.cs` test methods exercising the `WithReliability` surface | At least seven tests target the deleted entry point: `TryEvaluateRecordingAnchorRotationReliability_AnchorTerminalClamp_UsesReliabilityPathAndLogs` (`:799`), `_FailingSameChainSuccessor_BlocksPredecessorTerminalClamp` (`:834`), `_UnreliableTumblingParent_ReturnsDecision` (`:1323`), `_SmallOffsetTumblingParent_ReturnsReliable` (`:1360`), `_ExactWaypoint_ReturnsReliable` (`:1397`), `_RelativeParentRotationInterpolation_ReturnsUnreliable` (`:1433`), `_StableChildBracket_ParentBoundaryT_ReturnsUnevaluated` (`:1478`). Delete all of them. Audit for any others touching the WithReliability surface and delete those too. |
| `RelativeAnchorResolverContext.DebrisLocalOffsetSquaredMeters` field (`:47`), constructor parameter (`:60, :74`), and `WithDebrisLocalOffsetSquaredMeters` clone helper (`:77-91`) | Orphaned by the `WithReliability` tree deletion above. Only consumers (`:1910, :2095, :2106, :2116`) live inside the deleted tree. Delete the field, the constructor parameter, the assignment, and the clone helper. The new `TryResolveLiveAnchorTransform` parameter takes its place in the constructor signature. |
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

`TrajectorySidecarBinary.IsSupportedBinaryVersion` at `:403-416` is currently a hard-coded allowlist accepting `LegacyBinaryVersion` through `DebrisParentRecordingBinaryVersion`. Replace with a single-version allowlist:

```csharp
private static bool IsSupportedBinaryVersion(int version)
{
    return version == DebrisFrameContractBinaryVersion;
}
```

Single-version allowlist, NOT `>=`. The codec's per-version conditional reads (seam flag, ground clearance, etc.) only know how to interpret known stamps; a `>=` check would accept future stamps (v14+) that this reader can't actually parse. Future versions must be added to the allowlist explicitly when their layout is understood.

**Simplify all `binaryVersion >= X` gates** in `TrajectorySidecarBinary.cs` (~20 sites at `:431, :447, :479, :485, :515, :522, :557, :581, :735, :736, :752, :755, :773, :781, :783, :790, :899, :901, :906`). Under v13, `binaryVersion` is always `DebrisFrameContractBinaryVersion`; every `>=` gate becomes constant-true. Simplify the conditionals to their true branches as part of this commit (don't leave dead `else` branches behind — they're misleading and would suggest the codec still handles older formats). If the cleanup is too large for one commit, scope it to a follow-up commit and TODO-mark each gate.

The version-selection ladder at `:170-189` becomes unconditional v13:

```csharp
int binaryVersion = DebrisFrameContractBinaryVersion;
```

The reader's per-version conditional branches can be simplified now that only v13 is loadable.

### 5.3 ConfigNode codec rejection

`RecordingTreeRecordCodec.LoadRecordingFrom` (`Source/Parsek/RecordingTreeRecordCodec.cs:329`) has no version-rejection path today. The format-version parse currently lives inside the helper `LoadRecordingPlaybackAndLinkage` at `:432`, parsed at `:438-450`. Placing an early-exit inside the helper does NOT short-circuit `LoadRecordingFrom`'s continued call to `LoadRecordingResourceAndState` at `:422`.

The cleanest fix is to **lift the format-version parse out of the helper and into `LoadRecordingFrom` itself**, then early-exit before either helper runs:

```csharp
internal static void LoadRecordingFrom(ConfigNode recNode, Recording rec)
{
    // ... existing recording-id parse ...

    // Parse format version EARLY (moved from LoadRecordingPlaybackAndLinkage).
    if (recNode.TryGetValue("recordingFormatVersion", out string formatVersionStr)
        && int.TryParse(formatVersionStr, NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out int formatVersion))
    {
        rec.RecordingFormatVersion = formatVersion;
    }
    else
    {
        rec.RecordingFormatVersion = 0;
    }

    // v13 rejection gate: refuse loads of v12 and earlier.
    if (rec.RecordingFormatVersion < RecordingStore.DebrisFrameContractFormatVersion)
    {
        ParsekLog.Warn("Codec",
            $"LoadRecordingFrom: rejecting recording {rec.RecordingId} with " +
            $"recordingFormatVersion={rec.RecordingFormatVersion} " +
            $"(< DebrisFrameContractFormatVersion={RecordingStore.DebrisFrameContractFormatVersion}). " +
            "v12 and earlier are no longer loadable.");
        rec.RecordingFormatVersion = -1;  // sentinel for "rejected at load gate"
        // NOTE: a fresh `new Recording()` already has RecordingFormatVersion = 0
        // and a missing/corrupt recordingFormatVersion value would also parse to 0.
        // Use -1 to distinguish "rejected by this gate" from "legitimately zero
        // / never written" — the caller checks for -1 specifically.
        return;
    }

    LoadRecordingPlaybackAndLinkage(recNode, rec);  // version parse removed from this helper
    LoadRecordingResourceAndState(recNode, rec);
    // ... existing remainder ...
}
```

Caller-side check at the actual ConfigNode-codec consumer: **`RecordingTree.LoadFrom` at `RecordingTree.cs:199`** (citation correction from the prior draft, which incorrectly cited `RecordingSidecarStore.cs` — `grep -n "LoadRecordingFrom" RecordingSidecarStore.cs` returns zero hits; the actual call is from `RecordingTree`). After `LoadRecordingFrom` returns with `rec.RecordingFormatVersion == -1`:

1. **Skip `AddOrReplaceRecording`.** The recording's identity fields (`RecordingId`, `VesselName`, `TreeId`, `ExplicitStartUT`, etc. parsed at `:333-416`) are already populated but no playback/linkage/resource data exists. Adding a partial stub to the tree would leave an incoherent state. The caller must check `rec.RecordingFormatVersion == -1` (rejected) — NOT `== 0` (legitimate zero / never-written) — and skip `tree.AddOrReplaceRecording(rec)`.
2. **Surface the rejection.** A user-visible "unsupported format" message goes through the same UI surface that reports load errors today (depends on the call site context; implementer picks the right channel). The skip in step 1 is the safe behaviour; the message is in ADDITION, not instead of, the skip.

### 5.4 User-visible communication

Per the project's "private development, fresh and correct versions" policy, no migration tool is provided. v11 / v12 recordings are rejected at load. The user-facing surface:

- **CHANGELOG entry**: explicit "v13 recording format is incompatible with v12 and earlier; existing recordings will not load. Re-record under v13."
- **Load-time log lines**: every rejected recording emits the `ParsekLog.Warn("Codec", ...)` line above with the recording id and the actual format version, so the user can identify which saves are affected.
- **Caller-side message at `RecordingTree.cs:199` consumer**: surface the rejection in the UI rather than silently dropping (currently a TODO in §5.3 — implementer should pick the right notification surface).
- **No auto-migration**. The recorder state change (proximity-based section type) is recorder-side and cannot be reconstructed from v12 data.

### 5.5 Update `.claude/CLAUDE.md` format-version table

Add the new constant; mark v12 and earlier as no-longer-loadable. Reference `TrajectorySidecarBinary.IsSupportedBinaryVersion` and `RecordingTreeRecordCodec.LoadRecordingFrom` as the load-gate sites.

## 6. Concrete touchpoints

### 6.1 Recorder

| File | Lines | Change |
|---|---|---|
| `BackgroundRecorder.cs` | `:4295-4303` | `UpdateBackgroundAnchorDetection` early-return: gate the `ApplyDebrisAnchorContractToState` invocation on `parentDistance ≤ DebrisRelativeSectionExitMeters` (`≤ 550 m` for hysteresis-aware "stay-Relative"; `≤ 500 m` for "enter-Relative"). Outside that range, fall through to the standard non-anchor flow which will open an Absolute section. |
| `BackgroundRecorder.cs` | `:4600-4685` (`ApplyDebrisAnchorContractToState`) | Body unchanged — still anchors on parent, still seeds Relative — but invocation is now gated by §3.1 proximity. |
| `BackgroundRecorder.cs` | `:4722-4728` (third call site in `ApplyBackgroundRelativeOffset`) | Same proximity gate as `:4295-4303`. Inside 550 m → call; outside → skip. |
| `BackgroundRecorder.cs` | `:3338-3460` (debris seed in `InitializeLoadedState`) | Compute initial distance to parent. `≤ 500 m`: seed Relative-to-parent (existing path). `> 500 m`: seed Absolute via `CreateAbsoluteTrajectoryPointFromVessel`. |
| `BackgroundRecorder.cs` | `:1958-1967` | Wire debris proximity tier resolution per §3.1 pseudocode. |
| `BackgroundRecorder.cs` | new field block on `BackgroundVesselState` near `:202` | Add `debrisProximityTier`, `debrisProximityDistanceMeters`, `debrisProximityReason`. Do not add these to `BackgroundOnRailsState` (`:192-200`). |
| `BackgroundRecorder.cs` | new method | `ResolveDebrisParentDistanceMeters` static helper (debris-side guards + parent vessel lookup; cadence tier resolution itself delegates to the shared `ProximitySamplingCadence.Resolve`). |
| `BackgroundRecorder.cs` | `:1868-1878` and `:2046-2053` | Delete Re-Fly settle suppression for debris + `ShouldSuppressParentDebrisForReFlySettle` helper. |
| `BackgroundRecorder.cs` | `:4122-4128` (`ShouldPreferRootPartSurfacePoseForBackgroundSample`) | Keep; update comment to reference the v13 contract (root-part pose continuity still applies). |
| **New file:** `Source/Parsek/ProximitySamplingCadence.cs` | (new) | Shared module per §3.7. Holds `ProximitySamplingTier` enum and `ProximitySamplingCadence.Resolve` / `ProximitySamplingCadence.ResolveSampleInterval` static methods. |
| `FlightRecorder.cs` | `:29` (`ReFlyTreeSamplingCadence` enum) | Delete; replace all references with the shared `ProximitySamplingTier`. Mechanical rename across ~20-40 sites. |
| `FlightRecorder.cs` | `:752-819` (`ResolveActiveReFlyTreeSamplingCadence`) | Refactor: keep the Re-Fly-specific guards (marker / session / tree-id / ut-finite checks), then delegate tier resolution to `ProximitySamplingCadence.Resolve(proximityDistanceMeters, ReFlyTreeFullFidelityProximityRangeMeters, ReFlyTreeHalfFidelityProximityRangeMeters, out reason)`. Behaviour-preserving. |
| `FlightRecorder.cs` | `:867-876` (`ResolveReFlyTreeCadenceSampleInterval`) | Delete; replace call sites with `ProximitySamplingCadence.ResolveSampleInterval`. |
| `FlightRecorder.cs` | `:935` | `HighFidelityProximityRangeMeters = 250.0` (was `200.0`). |
| `FlightRecorder.cs` | `:821-865` | Update the cadence-aware `ResolveEffectiveMaxSampleInterval` AND `ResolveEffectiveMinSampleInterval` overloads to take two `ProximitySamplingTier` parameters (one for Re-Fly, one for debris). Re-Fly wins over debris when both non-None. **Both helpers must be extended**, not just max — the BG wiring change in the next row updates both call sites. |
| `BackgroundRecorder.cs` | `:1958-1967` (`effectiveMinSampleInterval` AND `effectiveMaxSampleInterval` wiring) | Update **both** assignments to pass the new `debrisTier` to the extended overloads. The previous draft only updated the max; without updating the min, debris at Full tier (≤ 250 m) would NOT actually sample at `configuredMin` — `effectiveMinSampleInterval` would fall through to the existing `proximityInterval` path. The promised Full-cadence behaviour requires both. Pseudocode:<br>`effectiveMinSampleInterval = FlightRecorder.ResolveEffectiveMinSampleInterval(ProximitySamplingTier.None, debrisTier, highFidelityActive, configuredMin, configuredMax);`<br>`effectiveMaxSampleInterval = FlightRecorder.ResolveEffectiveMaxSampleInterval(ProximitySamplingTier.None, debrisTier, highFidelityActive, configuredMax, configuredMin);` |
| `FlightRecorder.cs` | `:7106` (FG caller of the cadence-aware overload) | Update the call to pass `ProximitySamplingTier.None` for the debris slot (FG is not the debris recorder). Verify no other FG production callers of the cadence-aware overload exist via grep on `ResolveEffectiveMaxSampleInterval` / `ResolveEffectiveMinSampleInterval`; update each found. |
| `RecordingStore.cs` | `:105-114` | Add `DebrisFrameContractFormatVersion = 13`; bump `CurrentRecordingFormatVersion`. |

### 6.2 Renderer

| File | Lines | Change |
|---|---|---|
| `DebrisRelativePlaybackPolicy.cs` | `:191-199` (`ShouldRetireFromDiagnostic`) | Drop `"non-relative-section"` from the retire reasons. Under v13, debris in Absolute sections is the new majority case; coverage gate should NOT retire them. |
| `RelativeAnchorResolver.cs` | `:301-310` (single rejection site after WithReliability deletion — `:450-459` is inside the deleted tree) | (a) Relax loop-anchored rejection per §3.4: allow chain walk when `context.FocusRecordingId` resolves to an `IsDebris` recording. (b) On chain hops into a section with `anchorVesselId != 0u`, consult the new `context.TryResolveLiveAnchorTransform` callback and compose the live-anchor pose with the recorded offset. |
| `RelativeAnchorResolver.cs` | `RelativeAnchorResolverContext` definition | Add `Func<uint, string, double, (Vector3d pos, Quaternion rot)?> TryResolveLiveAnchorTransform` field. Populated by `ParsekFlight` at construction for flight-scene playback; left null at recorder-side construction (`BackgroundRecorder.cs:5021` — the recorder never walks a chain across the live-anchor boundary at sample time). The `string` argument is the victim recording id and must be forwarded to `TryResolveLoopLiveAnchorPose` for diagnostic trace context. |
| `RelativeAnchorResolver.cs` | (n/a — entirely covered by the `WithReliability` deletion row in §4.2 above) | Do NOT trim parameters from the `WithReliability` signatures listed at `:194, :269, :435, :668, :1045, :1398, :1534, :1898, :2028, :2126, :2138, :2342`. Those methods are deleted whole, not trimmed. |
| `GhostPlaybackEngine.cs` | `:2796-3006` | Delete `TryRouteAnchorRotationUnreliable`, `TryRouteAnchorRotationToShadow`. |
| `GhostPlaybackEvents.cs` | `:94+` (`AnchorRotationUnreliableRoute` enum) | Delete enum. Citation fix — prior draft cited `GhostPlaybackEngine.cs`; the enum actually lives in events. |
| `GhostPlaybackEngine.cs` | `:1150-1216` (`RenderInRangeGhost`), `:3297-3339` (`PositionLoopAtPlaybackUT`) | Delete the `anchorRotationRoute` branches. Add the v13 debris surface-selection branch before generic Relative positioning: Relative debris uses body-fixed primary (`TryPositionFromRelativeAbsoluteShadow` / renamed docs) unless a loop-anchored ancestor is detected; loop-ancestor cases invoke the standard Relative resolver and fall back to body-fixed primary on resolver failure. |
| `DebrisRelativePlaybackPolicy.cs` or new small helper near engine dispatch | new helper | Add a pure helper such as `ShouldAttemptLoopAnchoredDebrisChain(traj, section, playbackUT, tree/provisional context)` that returns true only when the debris parent chain reaches `LoopAnchorVesselId != 0u` at the active UT. Missing parent, parent Absolute/no active section, chain guard failure, or non-debris focus return false so the renderer stays on body-fixed primary. Unit-test this helper independently; it is the gate that prevents non-loop debris from reintroducing parent-rotation coupling. |
| `GhostPlaybackEngine.cs` | `:3297` (`PositionLoopAtPlaybackUT` return type) | Change return type from `AnchorRotationUnreliableRoute` to either `void` or `bool retired`. **Four** call sites consume the return value: `:1852, :2127, :2324, :2459` (`:2459` is in `PositionGhostAtLoopEndpoint` and already discards the return value, so it adapts trivially; the other three need explicit update). Update each call site to either ignore the void return or read the new `bool retired` flag. |
| `GhostPlaybackEngine.cs` | `:3348-3357` (`ResolveRenderSurface(AnchorRotationUnreliableRoute route, bool retired)`) | Delete or refactor. The route enum parameter is gone. Under v13 the surface is selected directly by the actual path: `BodyFixedPrimary` for debris body-fixed surface, `Legacy` for generic section dispatch, `Hidden` for retired/hidden, `Unknown` for unspecified. Either fold the helper inline at the four post-position trace sites (`:1226 / 1231, :1838, :2109, :2304`) or rewrite it to take explicit booleans such as `usedBodyFixedPrimary` / `retired`. |
| `GhostPlaybackEngine.cs` | `:4860-4862` (`ResolveRenderSurfaceForTesting`) | The test seam wraps `ResolveRenderSurface`. Delete or refactor in lockstep with `ResolveRenderSurface`. Affects any test file calling `ResolveRenderSurfaceForTesting`. |
| `GhostRenderTrace.cs` `RenderSurface` enum at `:63+` | Rename `Shadow` to `BodyFixedPrimary` (log string e.g. `"body-fixed-primary"`). The enum has **four** values today: `Unknown / Legacy / Shadow / Hidden`; after v13 it remains four values: `Unknown / Legacy / BodyFixedPrimary / Hidden`. Downstream `EmitPostUpdate` overloads + trace call sites pass `BodyFixedPrimary` when debris is positioned from the body-fixed surface. |
| `IGhostPositioner.cs` | `:65-77` (entire XML doc block on `TryPositionFromRelativeAbsoluteShadow`) | **Rewrite the entire doc block.** The current text is heavily entangled with deleted concepts ("tumbling-parent gate has classified [...] unreliable", "Phase D: recorded-data-only, never a substitute for live anchors", "reached only after the gate has positively classified the parent chain as visually unreliable", cross-ref to `AnchorRotationUnreliableRoute.Hidden`). Replace with a v13-accurate description: the method positions from the body-fixed primary surface stored alongside Relative debris samples; it is both the default path for non-loop Relative debris and the fallback when loop-chain composition fails. The signature at `:78-80` is unchanged. |
| `GhostPlaybackEngine.cs` | `:1148, :1249, :1256, :1418, :1661, :1847, :1861, :2124, :2134, :2223, :2320, :2331, :2380, :5203, :5242` | Delete reads / writes of `state.anchorRotationShadowRoutedThisFrame`. |
| `GhostPlaybackEngine.cs` | `:2977, :2983, :3031, :3136, :3163, :3223` | Delete reads / writes of `state.parentAnchoredDebrisCoverageRetired`. |
| `GhostPlaybackEngine.cs` | `:2715, :2717` | Update the `ShouldRetireOutsideAuthoredRelativeCoverage` consultation — still relevant for Relative debris sections (entered at 500 m, retained until 550 m), but now never fires for Absolute debris sections (per the policy fix above). |
| `GhostPlaybackEngine.cs` | `:5562, :5652, :5668` | `TryResolveInitialStructuralSeedBridgeEndUT` call sites — **keep** (init-hide mechanism per §4.1). Hides debris ghost mesh between the seed point and the first ordinary sample so no visible slide on activation. Independent of the deleted shadow-route / tumble-gate machinery; do not touch during v13 cleanup. |
| `GhostPlaybackState.cs` | `:93, :99, :152` | Delete `anchorRotationShadowRoutedThisFrame` field (`:93`), `parentAnchoredDebrisCoverageRetired` field (`:99`), and the reset of `anchorRotationShadowRoutedThisFrame` at `:152`. The `parentAnchoredDebrisCoverageRetired` field is deliberately NOT reset in `ResetGhostState` (the comment at `:153-156` explains why) — the deletion is the field itself, no other reset to update. |
| `GhostPlaybackEngine.cs` `shadowRouted` parameter on the post-position FX helpers and their call sites at `:1256, :1879, :2156, :2352, :3084, :3088, :3113, :3116` | Delete the parameter from helper signatures and from every call site that passed it. (Prior-draft `GhostPlaybackEvents.cs:89, :102` citation was wrong — those are XML-doc references inside the deleted-enum docstring, already covered by the enum-deletion row above. `shadowRouted` lives nowhere in `GhostPlaybackEvents.cs`.) |
| `ParsekFlight.cs` | `:15117-15246` (full range — citation in prior drafts was off; the function actually runs to `:15246`) | Delete `ShouldEvaluateAnchorRotationReliability`, `ShouldEvaluateAnchorRotationReliabilityForTesting`, `TryEvaluateAnchorRotationReliability`. |
| `ParsekFlight.cs` | `:832-834` (`anchorRotationHysteresis` field of type `Dictionary<AnchorRotationHysteresisKey, AnchorRotationHysteresisState>`) | Delete. The types live in `TumblingParentInterpolationGate.cs` being deleted whole; the field becomes a dangling reference. |
| `ParsekFlight.cs` | `:2038, :2087, :15761` | Delete the `anchorRotationHysteresis?.Clear()` calls. |
| `ParsekFlight.cs` | `:15117-15122` (the `tryEvaluateAnchorRotationReliability` delegate assignment inside `ComputePlaybackFlags`) | Delete. The delegate type is also being deleted (next row). |
| `GhostPlaybackEvents.cs` | `:77-82` (`TryEvaluateAnchorRotationReliability` delegate definition) | Delete. Single production caller in `ParsekFlight.ComputePlaybackFlags` is also deleted. |
| `GhostPlaybackEvents.cs` | `:94+` (`AnchorRotationUnreliableRoute` enum — citation fix; this enum lives in events, not engine as the prior draft cited) | Delete. |
| `GhostPlaybackEvents.cs` or wherever the delegate field lives | `TrajectoryPlaybackFlags.tryEvaluateAnchorRotationReliability` field | Delete. The field's value site (`ParsekFlight.cs:15117-15122`) is also being deleted in this commit; the field itself becomes unreferenced. |
| `ParsekFlight.cs` | `:16717-16810` | Keep `TryPositionFromRelativeAbsoluteShadow` impl — it is the body-fixed primary path for Relative debris sections and the loop-chain-failure fallback. Update the XML doc to reflect the new role (body-fixed primary inside the entry/hysteresis Relative window, not the v12 always-shadow / tumble-gate path). |
| `ParsekFlight.cs` | `:22857-22933` | Delete `TryUseLegacyDebrisShadowFallback` and `TryRetireParentAnchoredDebrisOnRecordedAnchorMiss`. |
| `ParsekFlight.cs` | `:22122, :22181, :22275` | Delete `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` call sites. |
| `ParsekFlight.cs` | `:22947-22961` | Delete the debris-only guard inside `TryUseRelativeAbsoluteShadowFallback`. The wrapper itself stays. |
| `ParsekFlight.cs` | `:14883-14888` (Re-Fly anchor hold reading `DebrisParentRecordingId`) | Keep — debris is still tied to parent for Re-Fly identity even under v13. Update comment. |
| `IGhostPositioner.cs` | `:78-80` | Keep `TryPositionFromRelativeAbsoluteShadow` signature — used by §3.2 fallback. |
| `RecordingFinalizationCacheApplier.cs` | `:119, :162, :169` | Update for v13's mixed Absolute+Relative debris sections. `TryGetLastAuthoredUT` should consider Absolute sections too, not early-return after Relative-only inspection. |

### 6.3 Codec

| File | Lines | Change |
|---|---|---|
| `TrajectorySidecarBinary.cs` | `:31, :71-72` | Add `DebrisFrameContractBinaryVersion`; update `CurrentBinaryVersion`. |
| `TrajectorySidecarBinary.cs` | `:170-189` | Replace version-selection ladder with unconditional v13 stamp. |
| `TrajectorySidecarBinary.cs` | `:403-416` (`IsSupportedBinaryVersion`) | Replace with `version == DebrisFrameContractBinaryVersion` (single-version allowlist, NOT `>=`). |
| `RecordingTreeRecordCodec.cs` | inside `LoadRecordingFrom` after `:443-449` | Add the `< DebrisFrameContractFormatVersion` rejection per §5.3. |
| `RecordingTree.cs` | `:199` (single call site of `LoadRecordingFrom`) | After return, check `rec.RecordingFormatVersion == -1` (rejected sentinel — distinguishes from `0` natural-default); if so, skip `tree.AddOrReplaceRecording(rec)` and surface a user-visible "unsupported format" message. (Prior-draft `RecordingSidecarStore.cs:234, :543, :751, :1234` citation was wrong — `grep -n "LoadRecordingFrom" RecordingSidecarStore.cs` returns zero hits.) |

### 6.4 Audited unchanged

| File | Verification |
|---|---|
| `AnchorDetector.cs` | `IsRecordingAnchorEligible` debris exclusion at `:240` stays. Comment at `:230-239` updated to reflect new contract. |
| `ParsekConfig.cs:30-65` | Unchanged. |
| `SessionMerger.cs` | `:149` carries `DebrisParentRecordingId`; `:803-1440` body-fixed-frame merge operates per-section and is correct under v13 (debris Relative sections have a parallel body-fixed list; debris Absolute sections don't). |
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

# Resolver loop-anchored carve-out — rejection should still fire for non-debris focus:
grep -rn "loop-anchor-out-of-scope" Source/Parsek/RelativeAnchorResolver.cs  # 1 hit after WithReliability deletion
# Verify the carve-out predicate is in place (focus is debris):
grep -rn "focusIsDebris\|focus.IsDebris" Source/Parsek/RelativeAnchorResolver.cs  # >= 1 hit at the remaining rejection site, plus the live-anchor leaf if factored inline

# Re-Fly safety net (non-loop live-PID audit still passes):
scripts/grep-audit-non-loop-live-pid.ps1

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
| `Debris_AtSeparation_WithinProximity_OpensRelative` | Debris created at 50 m from parent | First section is `Relative`, `anchorRecordingId == parent.RecordingId`; `bodyFixedFrames` populated alongside `frames` |
| `Debris_AtSeparation_BeyondProximity_OpensAbsolute` | Debris created at 600 m from parent (rare) | First section is `Absolute`; `bodyFixedFrames` null |
| `Debris_DriftsPastHysteresisExit_ClosesRelativeOpensAbsolute` | Debris drifts 490 → 560 m | At first sample where distance > 550 m: Relative closes, Absolute opens |
| `Debris_ReentersWithin500m_ReopensRelative` | Debris at 600 m drifts back to 490 m | Absolute closes, Relative opens, anchor pinned to parent |
| `Debris_HysteresisProtectsAgainstFlapping` | Debris hovering at 495-545 m | No section flip across the hysteresis band |
| `Debris_CadenceTier_Full_AtCloseRange` | Debris at 100 m | Effective `maxSampleInterval == configuredMin` |
| `Debris_CadenceTier_Half_AtMidRange` | Debris at 350 m | Effective `maxSampleInterval == min(configuredMax, 2 × configuredMin)` |
| `Debris_CadenceTier_None_AtFarRange` | Debris at 1500 m | Falls through to `ResolveDebrisAwareMaxSampleInterval` |
| `Debris_CadenceTier_ProportionalToDensity` | Debris at 100 m, Low vs High density | Sample-count ratio matches density-setting ratio |
| `Debris_CadenceTier_HalfNotClobberedByLegacyCap_Low` | Debris at 350 m with Low density (`configuredMin = 0.5 s`, `configuredMax = 8.0 s`) | Half-tier interval is `min(8.0, 2 × 0.5) = 1.0 s`, not capped at legacy 0.5 s |
| `Debris_CadenceTier_HalfNotClobberedByLegacyCap_Medium` | Debris at 350 m with Medium density (`configuredMin = 0.2 s`, `configuredMax = 3.0 s`) | Half-tier interval is `min(3.0, 0.4) = 0.4 s` |
| `Debris_CadenceTier_HalfNotClobberedByLegacyCap_High` | Debris at 350 m with High density (`configuredMin = 0.05 s`, `configuredMax = 1.0 s`) | Half-tier interval is `min(1.0, 0.1) = 0.1 s` |
| `Debris_CadenceComposition_OutOfProximity_AppliesLegacyCap` | Debris at 1500 m (None tier) with Low density | Effective `maxSampleInterval` is capped by `ResolveDebrisAwareMaxSampleInterval` at 0.5 s — the legacy cap fires only when proximity tier is None |
| `Debris_CadenceOrthogonality_Matrix` (9 combinations) | Cross-product `reFlyTier × debrisTier` (both `ProximitySamplingTier`) | Priority: Re-Fly Full > Re-Fly Half > debris Full > debris Half > existing fallback |
| `ProximitySamplingCadence_Resolve_PureFunction` (table-driven) | Distance × thresholds matrix | `Resolve(0, 250, 500) == Full`, `Resolve(100, 250, 500) == Full`, `Resolve(250, 250, 500) == Full`, `Resolve(251, 250, 500) == Half`, `Resolve(500, 250, 500) == Half`, `Resolve(501, 250, 500) == None`, `Resolve(NaN, ...) == None`, `Resolve(-1, ...) == Full` (negative distance counts as inside) |
| `ProximitySamplingCadence_ResolveSampleInterval_TierMath` | Full / Half / None × configuredMin / configuredMax matrix | `ResolveSampleInterval(Full, 0.1f, 0.5f) == 0.1f`, `ResolveSampleInterval(Half, 0.1f, 0.5f) == 0.2f`, `ResolveSampleInterval(Half, 0.3f, 0.5f) == 0.5f` (clamped to max), `ResolveSampleInterval(None, 0.1f, 0.5f) == 0.5f` |
| `ReFlyResolver_RefactorEqualsOldBehaviour` (regression test) | Sample of pre-refactor inputs → expected outputs | Re-Fly resolver via shared module produces identical results to the pre-refactor implementation across the full input space (marker present/absent, session/tree mismatches, distance buckets) |
| `Debris_AnchorIdentityAlwaysParent_InProximity` | Debris entering the Relative window with sibling debris closer than parent | Anchor remains parent (no nearest-search) |
| `Debris_BothSurfacesWrittenInsideProximity` | Debris at 100 m | `frames` contains anchor-local offsets; `bodyFixedFrames` contains body-fixed coords; same UT, same count |
| `Debris_DebrisParentRecordingId_StillSetOnSplit` | Standard separation | `child.DebrisParentRecordingId == parent.RecordingId` |
| `Debris_LoopAnchorVesselId_NotInherited` | Debris from loop-anchored parent | `child.LoopAnchorVesselId == 0u` (chain composition handles loop semantics) |
| `Debris_ParentDestroyed_EndsRecordingViaTTL` | Parent destroyed mid-recording | `CheckDebrisTTL` ends the debris recording; no new partial Relative samples are authored after the parent is gone |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection |
| `Debris_CascadeSeparation_ParentIdIsImmediateParent` | Debris A sheds B | `B.DebrisParentRecordingId == A.RecordingId` |
| `Debris_CascadeSeparation_DepthCapped` | Cascade depth > `MaxRecordingGeneration` | Further generations dropped (existing rule) |
| `Debris_ReFlySettleSuppression_Removed` | Debris of a Re-Fly settle parent | Samples continue (no suppression gap) |
| `Debris_NoParentRecordingId_FallsThrough` | Debris recording with `IsDebris == true && DebrisParentRecordingId == null` (orphan / corrupt state) | Cadence guard returns None; falls through to legacy `ResolveDebrisAwareMaxSampleInterval` cap |
| `Debris_OrphanParent_MissingFromTree_EndsRecordingViaTTL` | Debris with `DebrisParentRecordingId` pointing to a missing recording | `CheckDebrisTTL` ends the debris recording; if the distance helper is called first it returns NaN and emits `None`, but no continuing adaptive recording is expected |
| `Debris_HysteresisExitZone_LowDensity_FallsThroughToLegacyCap` | Debris at 520 m on Low density (in the 500–550 m hysteresis transit, frame is still Relative but tier is None per §3.1) | Effective `maxSampleInterval` is the legacy 0.5 s cap (None tier triggers the legacy fallthrough), NOT 8 s `configuredMax` |
| `Debris_BoundaryPointContinuity_AtFrameTransition` | Debris crosses 550 m, Relative → Absolute | Last `bodyFixedFrames` body-fixed position equals first new Absolute section `frames` within 0.1 m |

### 7.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AbsoluteSection_StandardDispatch` | Debris in Absolute section | `InterpolateAndPosition` invoked; no anchor lookup |
| `Debris_RelativeSection_NonLoop_UsesBodyFixedPrimary` | Debris in Relative section, parent resolvable, no loop-anchored ancestor | `TryPositionFromRelativeAbsoluteShadow` reads `bodyFixedFrames`; `RelativeAnchorResolver` is not invoked |
| `Debris_RelativeSection_LoopChainFails_FallsBackToBodyFixedPrimary` | Debris in Relative section, loop-anchored ancestor detected but live anchor/callback missing | Falls back to `TryPositionFromRelativeAbsoluteShadow` reading `bodyFixedFrames`; no coverage retirement while body-fixed data covers |
| `Debris_RelativeSection_LoopAnchoredAncestor_ResolvesThroughChain` | F (debris) in Relative on T (tanker, loop-anchored to S) | F's render position = `(S.livePose × T.recordedOffsetToS) × F.recordedOffsetToT` |
| `Debris_RelativeSection_LoopAnchoredAncestor_AcrossIterations` | F as above, sampled at loop iterations 1 and 5 | F's rendered position differs between iterations (follows S's live pose advance) |
| `Debris_ParentTumbling_AbsoluteSection_StillRenders` | Debris in Absolute, parent rotating at 200 °/s | Standard absolute path runs; debris remains visible (no tumble gate to fire) |
| `Debris_ParentDestroyed_AbsoluteSection_StillRenders` | Debris in Absolute, parent recording absent | Renders normally; no visibility coupling |
| `Debris_NotRetiredByCoverageGate_AbsoluteSection` | Debris in Absolute with `frames.Count > 0` | `ShouldRetireOutsideAuthoredRelativeCoverage` does NOT fire |
| `Debris_LongRange_NoSyncedSwing` | Multiple debris pieces all in Absolute, parent rotating | Per-frame position deltas independent of shared rotation channel |
| `RelativeAnchorResolver_DebrisChild_AnchoredOnLoopAnchoredParent_Allowed` | F (debris) anchored on T (loop-anchored) | Resolver returns success (carve-out per §3.4); non-debris anchored on loop-anchored still rejected (regression test) |
| `RelativeAnchorResolver_CascadeDebris_AnchoredOnLoopAnchoredGrandparent_Allowed` | G (debris focus, parent F) anchored on F (debris, parent T) anchored on T (loop-anchored to S) | Carve-out fires at the T rejection during chain walk. Resolver composes G's position via T's live-anchor callback. Cascade-correctness regression test for the focus-is-debris predicate. |
| `RelativeAnchorResolver_LiveAnchorCallback_ReturnsCorrectPose` | F debris on T loop-anchored to S; `TryResolveLiveAnchorTransform` populated, S at known live pose | Resolver invokes callback for T's section anchored on S; composes T_pose = `S.liveTransform × T.recordedOffsetToS`; F_pose = `T_pose × F.recordedOffsetToT`. Assert against expected world position. |
| `RelativeAnchorResolver_LiveAnchorCallbackNull_ReturnsFalseForEngineFallback` | Same setup but callback null (recorder-side context) | Carve-out reaches the live-anchor leaf, resolver returns false with a diagnostic reason, and the engine-level v13 path falls back to body-fixed primary |
| `ParsekFlight_BuildFlightRelativeAnchorResolverContext_PopulatesLiveAnchorCallback` | Production-side static `BuildFlightRelativeAnchorResolverContext` path using `ParsekFlight.TryGetLiveAnchorTransformDelegate()` | Assert the returned context's `TryResolveLiveAnchorTransform` is non-null when `ParsekFlight.Instance` is available and forwards `(anchorPid, victimRecordingId, ut)` to `TryResolveLoopLiveAnchorPose`. Without this test an implementer could correctly wire the resolver-side carve-out and still ship a null callback in the production context. |

### 7.3 Format-version unit tests

| Test | Setup | Assertion |
|---|---|---|
| `V12Recording_LoadFails_BinaryCodec` | Synthetic v12 .prec | `Probe.Supported == false`; load rejected |
| `V11Recording_LoadFails_BinaryCodec` | Synthetic v11 .prec | Same |
| `V12Recording_LoadFails_ConfigNodeCodec` | Synthetic v12 ConfigNode | `LoadRecordingFrom` sets `rec.RecordingFormatVersion = -1`; caller skips |
| `V13Recording_RoundTrip` | Write v13, reload | Identical content; `RecordingFormatVersion == 13` |
| `V13Probe_FormatVersionMatchesRecording` | Newly-written v13 sidecar | `Probe.FormatVersion == rec.RecordingFormatVersion == 13` |

### 7.4 In-game tests

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_BothSurfacesRecorded` | Booster separation; debris within proximity | Logs show Relative section open with `frames` + `bodyFixedFrames` populated |
| `Debris_SeparationEvent_NoVisibleSlideAtInit` | Booster separation; trace the first 5-10 render frames after the debris ghost activates | Ghost mesh is hidden (or transform held at the seed pose) until the first ordinary sample lands, then becomes visible at the correct world position. No visible slide from origin/seed to first-sample pose. Regression test for the `TryResolveInitialStructuralSeedBridgeEndUT` init-hide mechanism (§4.1). |
| `Debris_SeparationEvent_DriftingAway_TransitionsToAbsolute` | Debris drifts past 550 m | Log shows `DEBRIS RELATIVE exit:` event; subsequent samples in Absolute section |
| `Debris_LoopAnchoredAncestor_FollowsLiveAnchor` | Tanker approach to station, loop the recording, fairing jettisoned mid-approach | Across loop iterations, fairing's rendered position follows the station's live orbital position (verified by frame trace at iterations 1, 3, 5) |
| `Debris_LongRange_TumblingParent_NoArtifact` | Parent at 200 °/s, debris > 550 m (Absolute) | Debris ghost renders smoothly (no synced-rotation channel) |
| `Debris_CloseRange_TumblingParent_NonLoopBodyFixedPrimary` | Parent at 200 °/s, debris < 500 m (Relative), no loop-anchored ancestor | Body-fixed primary path renders; parent rotation does not drive debris position |
| `Debris_MidRange_LoopChain_TumblingParent_HalfCadenceArtifact_VisibleOrNot` | Parent at 200 °/s, debris 250–500 m (Relative, Half cadence), loop-anchored ancestor present | Document the loop-chain visual artifact (if any) — this is the tunable regime per §9 |
| `Debris_PhysicsWarp_FrameBoundary_NoArtifact` | Debris crossing 550 m during 2× physics warp | Cadence transitions cleanly |
| `Debris_CadenceProportionalToDensity` | Same scenario at Low vs High density | Sample count in 0–250 m window scales with density |

In-game test files affected by field deletions (`anchorRotationShadowRoutedThisFrame`, `parentAnchoredDebrisCoverageRetired`): `RuntimeTests.cs:4278, 4390-4391, 4524, 4660` — rewrite or remove the shadow-route assertions.

### 7.5 Test fixture

`Source/Parsek.Tests/Generators/DebrisFrameContractRecordingFixture.cs`: builds a synthetic debris recording with:
- Mixed Absolute (outside proximity) and Relative (inside proximity) sections covering a drift sequence
- `frames` + `bodyFixedFrames` populated correctly inside Relative sections
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
| `DebrisParentSamplingCeilingTests.cs` | Rewrite. Asserts current cadence composition (uses `ResolveDebrisAwareMaxSampleInterval`, `MidInterval`, `HighFidelityProximityRangeMeters`); the new v13 composition rule (proximity tier supersedes legacy cap when non-None) changes most assertions. |
| `DebrisRelativeCoveragePrimitivesTests.cs` | Review for v13 mixed-section debris. |
| `RuntimeTests.cs` (in-game) | Rewrite assertions on deleted fields. |
| `LegacyMigrationTests.cs` | **Delete** or **gut to compile-only stubs**. The entire file is about migrating from old versions; under v13 the load gate rejects v12 and earlier, so legacy-migration test coverage is moot. If the file tests v13→v13 round-trip behaviour, retain those cases under a renamed file. |
| `LoopAnchorTests.cs` | **Update**. All test fixtures need `recordingFormatVersion = 13` stamps. The §3.4 carve-out introduces new pass cases (debris focus succeeds on loop-anchored target); some existing "rejected" assertions may flip — audit each. |
| `AdaptiveSamplingTests.cs` | **Update**. The existing assertions track Re-Fly cadence behaviour; the shared-module refactor preserves output (reason strings via the §3.7 mapping table), so the harness keeps working but reason-string assertions need an explicit "old vs new are equal" check. |

**Test-fixture sweep for v13 format stamps.** §5.3 rejects recordings with `RecordingFormatVersion < 13`. Many test files construct ConfigNode fixtures without an explicit `recordingFormatVersion` (defaults to 0) or with v11/v12 layouts. All such fixtures need v13 stamps in setup. Affected files include (non-exhaustive — implementer greps `Source/Parsek.Tests/` for `LoadRecordingFrom` call sites and `recordingFormatVersion` fixture values):

- `LegacyMigrationTests.cs`, `LoopAnchorTests.cs`, `RecordingFieldExtensionTests.cs`
- `Bug270SidecarEpochTests.cs`, `Bug156Tests.cs`, `Bug362TerminalCrashDebrisTests.cs`
- `TerrainCorrectorTests.cs`, `TreeCommitTests.cs`, `KerbalEndStateTests.cs`
- `RewindSpawnSuppressionTests.cs`, `BackgroundSplitTests.cs`, `GhostOnlyRecordingTests.cs`
- Test fixture builders in `Source/Parsek.Tests/Generators/`

For each: either bump the stamp to 13 or route the fixture through a "test only" load path that bypasses the v13 gate. The latter is a one-line addition to `RecordingTreeRecordCodec` (an internal `LoadRecordingFromForTesting` that skips the gate); use this for tests that specifically exercise v11/v12-layout edge cases (rare; mostly `LegacyMigrationTests` if any cases survive).

## 8. Rollout

Land as a single PR or split per the project's preference:

1. **Format version bump + codec rejection** (commit 1). Constants + `IsSupportedBinaryVersion` prune + `LoadRecordingFrom` early-exit + `RecordingTree.cs:199` consumer surface. After this commit, v12/v11 saves are rejected at load.
2. **Renderer cleanup** (commit 2). Delete the always-shadow router, the tumble-gate predicates and helpers, `LegacyDebrisShadowGate`, `TumblingParentInterpolationGate`, the `WithReliability` parallel resolver tree, the two state fields and their read/write sites, the `anchorRotationHysteresis` field + `.Clear()` calls, the `TryEvaluateAnchorRotationReliability` delegate, the `AnchorRotationUnreliableRoute` enum, and the `TrajectoryPlaybackFlags.tryEvaluateAnchorRotationReliability` field. Compile clean; some debris-related tests fail (rewritten in commit 5).
3. **Field rename + comment rebranding** (commit 3). Rename `TrackSection.absoluteFrames` → `TrackSection.bodyFixedFrames` per §3.7. Replace "absolute shadow" / "shadow track" / "shadow payload" / etc wording with v13-correct framing across every site in §3.7's site table plus any additional hits from `grep -rn "absoluteFrames\|absolute shadow\|ABSOLUTE_POINT" Source/Parsek/`. This is a pure rename + comment update; behaviour unchanged. ConfigNode on-disk node name `ABSOLUTE_POINT` → `BODY_FIXED_POINT` at `TrajectoryTextSidecarCodec.cs:644, :1167, :1635-1643, :1785-1810` (NOT `RecordingTreeRecordCodec.cs:215`, which writes the unrelated `isDebris` flag).
4. **Resolver carve-out + live-anchor callback** (commit 4). Relax `RelativeAnchorResolver` loop-anchored rejection at the single remaining site (`:301-310`) per §3.4. Add `TryResolveLiveAnchorTransform` field to `RelativeAnchorResolverContext`. Populate from `ParsekFlight` host wiring; leave null at the recorder-side context construction at `BackgroundRecorder.cs:5021`. Add the live-anchor compose step inside `TryResolveRelativeSectionPose` at the `TryResolveSectionAnchorRecordingId == false && section.anchorVesselId != 0u` leaf. Add the cascade and live-anchor-callback resolver tests.
5. **Recorder change** (commit 5). Gate `ApplyDebrisAnchorContractToState` on proximity; simplify debris seed; add cadence tier resolver, state, and wiring; raise `HighFidelityProximityRangeMeters` to 250 m; remove Re-Fly settle suppression for debris; add the shared `ProximitySamplingCadence` module and refactor `ResolveActiveReFlyTreeSamplingCadence` to use it.
6. **Tests** (commit 6): test fixture, recorder unit tests, renderer unit tests, format-version tests, in-game test rewrites.
7. **Docs** (commit 7): CHANGELOG; `.claude/CLAUDE.md` (format-version table, debris-shadow notes, frame-contract paragraphs); close relevant `docs/dev/todo-and-known-bugs.md` entries; **sweep the debris-related design docs** for v12-era statements that v13 invalidates (see §8.1 below).

### 8.1 Design documents to review and update

The v13 contract changes the load-bearing assumptions behind several existing design docs. The implementer should audit each, mark v12-only claims as superseded, and either update inline or add a "v13 status" header noting which sections still apply:

| Doc | What v13 changes |
|---|---|
| `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md` (PR 3b §) | The "always Relative for debris lifetime" decision is reversed. Decision §5 (Option C, "always record Relative") no longer applies; the proximity-gated contract from §3.1 supersedes. Decision §7 ("debris bound to parent visibility") is relaxed for Absolute sections per §3.3 of this plan. |
| `docs/dev/plans/debris-always-shadow.md` (PR #803) | The "always-shadow" renderer path is deleted (§4.2). Synced-rotation rationale should be rewritten to note v13 prevents the failure mode structurally via the bounded proximity window rather than by routing through the shadow at render time. |
| `docs/dev/plans/debris-smooth-trajectory-during-tumble.md` (PR #800) | Tumble-gate is deleted (`TumblingParentInterpolationGate.cs` gone). The "smooth trajectory during parent tumble" decision was a v12-era workaround; under v13, the issue is bounded by recorder cadence + proximity window, not by renderer-time routing. |
| `docs/dev/plans/ghost-anchor-recording-chain-plan.md` | Debris-specific bullets need updating. Section §"Vessel symmetry" and §"Debris and other co-bubble vessels" reference the v12 always-Relative contract; rewrite to reflect v13's proximity-gated contract + anchor-pinned-to-parent. The loop-anchored chain composition (this plan's §3.4) is new and may need a paragraph in the chain plan's loop carve-out section. |
| `docs/dev/research/recording-and-ghost-policies-audit-2026-05-07.md` | Audit findings about v12 debris policy gaps may be partly addressed and partly invalidated by v13. Mark stale items as superseded by v13. |
| `docs/dev/research/trajectory-frame-overview-2026-05-11.md` | The trajectory-frame audit (the doc that motivated this plan). Case 2 ("Debris of regular main vessel"), Case 9/10 (Re-Fly debris cases), the case-by-case matrix, and §0.2 format version table all need updating for v13. |
| `docs/dev/plans/fix-debris-recorder-tail-invariant.md`, `fix-debris-ghost-initial-slide.md`, `fix-debris-relative-tail-retirement.md`, `fix-watch-debris-retirement.md`, `investigate-radial-debris-origin.md` | v12-era debris fix plans. Each needs a quick review for whether the underlying issue is (a) still present under v13, (b) resolved by v13 structurally, or (c) moot because the relevant code path is deleted. |
| Any other `docs/dev/plans/*debris*.md` or `docs/dev/research/*debris*` doc that hits via `grep -rli "debris" docs/dev/` | Same sweep; some are pre-v12 and may still apply; others are v12-specific and need v13 status. |

Recommended sweep procedure:

```bash
# Find every dev doc mentioning debris or shadow framing:
grep -rln "debris\|absoluteFrames\|absolute shadow\|always-shadow" docs/dev/

# For each: open, audit for v12-only claims (always-Relative, always-shadow,
# tumble-gate, LoopAnchorVesselId inheritance on debris, etc.), and either
# update inline or add a top-of-doc "Superseded by v13" pointer to this plan.
```

The CLAUDE.md update is mandatory (it's the working reference for future agents and reviewers); the others are best-effort but should be done as part of the v13 PR rather than deferred to a follow-up — stale design docs are corrosive to future work on this surface.

Pre-merge:
- `dotnet test` all green
- Re-Fly in-game tests pass (scope-creep gate)
- §6.6 grep audits return expected results
- `scripts/grep-audit-*.ps1` clean
- Playtest: separation events, cadence transitions, the canonical tanker-approach-to-station looped scenario with mid-approach jettison, long-range drift, density-setting proportionality

## 9. Out of scope (follow-ups)

- **Future recorders using the shared cadence module.** The `ProximitySamplingCadence` module (§3.7) is reusable. Candidate future consumers: a co-bubble formation-flying recorder, an approach-corridor recorder near a station, a science-rover proximity recorder. Each supplies its own thresholds and guards. Not in scope for this PR but the module's API is designed for it.
- **BG-parent hi-fi sampling extension**: extending `RegisterHighFidelityProximityVessel` to BG-recorded parents of in-proximity debris. Useful if playtest reveals chain-math jitter due to sparse parent samples inside the 500 m window. The body-fixed shadow on the same Relative section is a safety net regardless.
- **Tunables for the 250–500 m Half-cadence Relative window**: this regime is the highest-error zone when the renderer deliberately takes the loop-chain path (lever arm up to 500 m × parent slerp at 2 × min cadence). **Explicit cost acknowledgment:** the 250–500 m Half-cadence Relative window is a deliberate storage increase relative to the earlier 250 m-only design. Non-loop debris renders from body-fixed primary, so it does not pay synced-rotation render cost; loop-anchored debris gets the benefit and the risk. The user has accepted this trade-off explicitly ("we can always tune later"). If playtest shows the loop-chain cost is visible, options to address:
  1. Drop the cadence in 250–500 m back to Full (uniform Full inside the whole Relative window) — doubles storage in the outer ring.
  2. Reinstate the tumble-gate as a renderer-time shadow fallback (PR #803-style routing) but limited to high parent rates inside the Relative window.
  3. Recorder-side rate filter: skip Relative-offset emission (keep only body-fixed primary) when parent rate exceeds threshold inside 250–500 m.
  4. Shrink the Relative window back to 250 m total.
  None of these are needed if the user-visible artifact is acceptable — "we can tune later" per user direction.
- **Visibility decoupling product validation**: under v13 debris in Absolute renders independent of parent. If playtest prefers retiring debris when parent is destroyed, a follow-up adds the check.
- **`DebrisParentRecordingId` UI surfacing**: filter / group debris by parent in the recordings table.
- **Recorder denser sampling for fast-debris-rotation cases**: independent concern affecting debris attitude, not position.
- **Resolver carve-out generalization**: currently the loop-anchored-as-anchor allowance is debris-only. If non-debris cases emerge that legitimately want to chain through a loop-anchored ancestor, generalize the gate.

## 10. Plan evolution

Thirteen drafts on the branch arrived at this design:

- `4b9a9bd` — first draft: proximity-gated 2300/2500 m, revert PR #803.
- `257ff31` — second draft (first review): keep PR #803 inside bounded Relative window.
- `17cb5db` — third draft (user feedback): absolute-primary always; tiered cadence; experimental 0–250 m Relative shadow.
- `4826ede` — fourth draft (first Opus review): drop experimental Relative; debris Absolute-only.
- `3d9a5fe` / `bc85214` — fifth draft (second Opus review + loop-anchored debris): debris inherits `LoopAnchorVesselId` from loop-anchored parent.
- `9662558` — sixth draft: user's insight that *every* debris recording within proximity should carry the relative-to-parent data; chain composition through loop-anchored ancestors handles the loop case naturally without `LoopAnchorVesselId` inheritance. No experimental framing — the relative surface has a concrete consumer (loop scenarios) and a real benefit (sub-metre geometric accuracy at close range). Relative window was 250 m.
- `ede8620` — seventh draft: Relative shadow window extended to 500 m total, with Half cadence in 250–500 m. Frame hysteresis exit moved 280 m → 550 m. §9 enumerates remediation options if playtest shows artifacts in the 250–500 m + tumbling-parent regime.
- `a995001` — eighth draft: proximity-cadence mechanism extracted as a shared `ProximitySamplingCadence` module (per user direction: "we should extract this proximity cadence mechanism as a module that can be used by any recorder"). The old `FlightRecorder.ReFlyTreeSamplingCadence` enum and resolver are refactored to delegate tier resolution to the shared module (behaviour-preserving; same thresholds, same tier semantics). Debris uses the same module with its own thresholds. Adds one Re-Fly touch — the cadence resolver — but observable behaviour is preserved by construction (regression test included).
- `75c810f` — ninth draft: second Opus review applied. Resolver carve-out cascade-correct predicate (focus-is-debris); cadence composition with legacy `ResolveDebrisAwareMaxSampleInterval` made explicit; WithReliability resolver tree (9 methods, ~500 lines) explicitly enumerated for deletion plus named test methods; user-visible communication for v11/v12 rejection documented; packed-parent description fixed; 250–500 m Half-cadence regression cost acknowledged; multiple citation fixes.
- `2c4d4e3` — tenth draft: third Opus review applied. Added live-anchor callback (initial design); field rename `absoluteFrames` → `bodyFixedFrames` and "absolute is primary, relative is shadow" comment rebranding; added missing compile-breaking deletion rows; fixed `RecordingTree.cs:199` citation; added §8.1 design-doc sweep.
- `46e4c2a` — eleventh draft: fourth Opus review applied. Two P0s and several P1s:
  - **P0-1 (architectural)**: the previous draft's live-anchor-compose pseudocode hooked the wrong field. `TryResolveRecordingPose` dispatches by `section.referenceFrame`; the chain walk does NOT iterate `section.anchorVesselId` at that level. The correct hook is inside `TryResolveRelativeSectionPose` (`RelativeAnchorResolver.cs:1926-2018`) at the `legacy-anchor-recording-id-missing` leaf at `:1945-1955`: when `TryResolveSectionAnchorRecordingId` returns false AND `section.anchorVesselId != 0u` AND focus is debris AND the callback is populated, compose live-anchor pose × section's anchor-local offset. The §3.4 pseudocode is now grounded in the actual call graph.
  - **P0-2 (compile error)**: `RelativeAnchorResolverContext` is `internal readonly struct`. The previous draft showed a mutable field, which won't compile. Rewrote as `public readonly Func<...>` + new constructor parameter + line in `WithDebrisLocalOffsetSquaredMeters` clone helper. Also decided host wiring: `BuildRelativeAnchorResolverContext` becomes an instance method on `ParsekFlight`, callable callback is a lambda that adapts the private `RelativeAnchorPose` struct to the `(Vector3d, Quaternion)?` callback signature.
  - **P1 (Re-Fly reasons)**: shared module returns generic reason strings (`"full"`, `"half"`, etc); existing Re-Fly tooling expects `"active-refly-tree-full"`, `"active-refly-tree-half"`, etc. Re-Fly guard wrapper post-translates per a 4-row mapping table.
  - **P1 (enum deletion fallout)**: `AnchorRotationUnreliableRoute` deletion has consumers beyond the router: `PositionLoopAtPlaybackUT` return type (three call sites consuming the enum), `ResolveRenderSurface` helper (four post-position trace sites), `IGhostPositioner.cs:71` XML doc, `GhostRenderTrace.RenderSurface.Shadow` enum value. Added explicit deletion rows for each.
  - **P1 (shadowRouted location)**: prior draft cited `GhostPlaybackEvents.cs:89, :102` for the parameter sites. Those are XML-doc references inside the deleted-enum docstring (already covered by the enum-deletion row). The actual parameter sites are in `GhostPlaybackEngine.cs:1256, :1879, :2156, :2352, :3084, :3088, :3113, :3116`. Fixed.
  - **P1 (codec rename location)**: prior draft cited `RecordingTreeRecordCodec.cs:215` for the ConfigNode codec column rename. That line is `recNode.AddValue("isDebris", ...)` — unrelated. The actual `absoluteFrames` codec sites are in `TrajectoryTextSidecarCodec.cs:644, :1167, :1635-1643, :1785-1810`. On-disk node name `ABSOLUTE_POINT` renames to `BODY_FIXED_POINT` under v13. Added `SessionMerger.cs` and `TrajectoryTextSidecarCodec.cs` rows to §3.7 rename site table.
  - **P2 (lingering `RecordingSidecarStore.cs` references)**: §5.3 already corrected the citation to `RecordingTree.cs:199`, but §5.4 and §6.3 still cited the wrong file. Fixed.
  - **P3 (host-wiring test)**: added `ParsekFlight_BuildRelativeAnchorResolverContext_PopulatesLiveAnchorCallback` to §7.2 — without this test the implementer could correctly wire the resolver-side carve-out and still ship a null callback in the production context.
  - **Various citation drifts** (`:5246-5275` → `:5269-5275`, `:21070-21160` → `:21062-21160`, `:22121, :22180, :22274` → `:22122, :22181, :22275`).
- `6d3ec20` — twelfth draft: fifth Opus review applied. Three P0/P1 corrections that would have caused compile errors or silent mis-renders:
  - **P0-1 (compile error)**: the live-anchor callback signature was `(uint, double)`, but `TryResolveLoopLiveAnchorPose` actually takes `(uint anchorVesselId, string victimRecordingId, double targetUT)` — three input params plus the out parameter. The adapter lambda as previously written wouldn't compile, and would have stripped the victim-recording-id from the diagnostic trace context even if it did. Also fixed: the adapter read `pose.worldPosition` / `pose.worldRotation`; the actual struct fields are `worldPos` / `worldRotation` at `ParsekFlight.cs:309-310`. Callback signature updated to `Func<uint, string, double, (Vector3d pos, Quaternion rot)?>`; hook updated to pass `recording.RecordingId` as the victim id; adapter reads `pose.worldPos`.
  - **P0-2 (silent mis-render)**: there is a second `RelativeAnchorResolverContext` construction site at `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:471` (the `TryBuildRelativeAnchorResolverContext` static helper), reached on the rendering hot path. The previous draft only inventoried the ParsekFlight sites. Without populating the callback at the Production site, debris-on-loop-anchored renders through Production would silently fall through to the shadow fallback. Added a complete site table to §3.4 enumerating all six `new RelativeAnchorResolverContext` sites and their dispositions (populate vs leave null). The method on ParsekFlight is also named `BuildFlightRelativeAnchorResolverContext`, not `BuildRelativeAnchorResolverContext` as previously cited.
  - **P0-3 (enumeration completeness)**: `PositionLoopAtPlaybackUT` has FOUR callers (`:1852, :2127, :2324, :2459`), not three. The fourth at `:2459` discards the return value today so it adapts trivially, but the plan's enumeration was off and would have confused an implementer. Also: `ResolveRenderSurfaceForTesting` at `GhostPlaybackEngine.cs:4860-4862` is a fifth caller of `ResolveRenderSurface` (a test seam) that the plan didn't enumerate — added explicit deletion row.
  - **P1 (TryGetFocusRecording contract)**: pseudocode in §3.4 called `TryGetFocusRecording` without defining its signature or resolution order. An implementer would have had to invent the contract. Added a concrete pseudocode block specifying lookup order: `ProvisionalRecordings` → `FocusTree.Recordings` → `PendingTree?.Recordings`, with null `FocusRecordingId` returning false.
  - **P1 (test fixture sweep)**: `LoadRecordingFrom` is called from 40+ test sites across `LegacyMigrationTests.cs`, `LoopAnchorTests.cs`, `AdaptiveSamplingTests.cs`, and many others. Under §5.3 each fixture without an explicit `recordingFormatVersion = 13` would silently fail. Added an explicit test-fixture-sweep paragraph in §7.6 enumerating affected files and recommending a `LoadRecordingFromForTesting` internal seam for the rare v11/v12-edge-case tests that need to bypass the gate. Also added explicit dispositions for `LegacyMigrationTests.cs` (delete or gut), `LoopAnchorTests.cs` (update fixtures + audit assertions that may flip), `AdaptiveSamplingTests.cs` (update + verify reason-string parity).
  - **P1 (`IsSupportedBinaryVersion`)**: tightened from `>=` to `==` (single-version allowlist). Future versions need explicit allowlist entries when their layout is understood; `>=` would let unknown future stamps through.
  - **P1 (IGhostPositioner doc rewrite)**: previously cited `:78-80` line range; the actual stale doc block runs `:65-77` and is heavily entangled with deleted concepts (tumbling-parent gate, Phase D, AnchorRotationUnreliableRoute cross-ref). Expanded the scope to "rewrite the entire doc block at `:65-77`."
  - **P1 (Re-Fly guard ordering)**: added an explicit note in §3.7 that the existing `ResolveActiveReFlyTreeSamplingCadence` guard order must be preserved (the Re-Fly wrapper emits `proximity-missing` BEFORE the tier dispatch, after `tree-mismatch`). The §7.1 regression test covers any drift.
  - **P2 (commit-3 citation drift)**: §8 commit 3 still cited `RecordingTreeRecordCodec.cs:215` for the codec rename despite §3.7 correcting it to `TrajectoryTextSidecarCodec.cs`. Fixed.
  - **P3 (RenderSurface enum values)**: previously said "currently has `Legacy / Shadow / Hidden`"; actual enum has FOUR values (`Unknown / Legacy / Shadow / Hidden`). Updated the deletion row to clarify `Unknown` stays.
- **current** — thirteenth draft: sixth Opus review applied. Four P0 fixes and several P1s, mostly closing gaps that would have caused compile failures or stranded dead code:
  - **P0-1 (host-wiring contradiction)**: §3.4 simultaneously proposed making `BuildFlightRelativeAnchorResolverContext` an instance method AND exposing a static `ParsekFlight.GetLiveAnchorTransformCallback()` for the Production site. Resolved by picking the static-accessor approach (`ParsekFlight.TryGetLiveAnchorTransformDelegate()` reading the `Instance` singleton at `:174`), keeping `Build` static, no instance-method cascade through `TryBuildRelativeAnchorResolverContext` at `:22794` and its callers (`:17943, :18098`). Both production sites (`ParsekFlight.cs:15483` and `ProductionAnchorWorldFrameResolver.cs:471`) call the same accessor.
  - **P0-2 (orphan plumbing)**: `RelativeAnchorResolverContext.DebrisLocalOffsetSquaredMeters` field, constructor parameter, and `WithDebrisLocalOffsetSquaredMeters` clone helper are all consumed only inside the `WithReliability` parallel resolver tree being deleted. They become dead code after the §4.2 deletion. Added explicit deletion row to §4.2. The new `TryResolveLiveAnchorTransform` parameter takes the old slot in the constructor signature; no clone helper needed (no caller would re-clone with a different callback value).
  - **P0-3 (rename misses)**: the "no change at binary codec" claim was wrong at the C# level — `TrajectorySidecarBinary.cs` reads/writes `track.absoluteFrames` at six sites (`:386, :388, :389, :756, :785, :791`) that all need to be renamed when the C# field renames (wire format unchanged, but C# source expressions still rename). Added missing rename sites: `TrajectorySidecarBinary.cs:386-791` (field reads), `IncompleteBallisticSceneExitFinalizer.cs:731, :951` (log strings consumed by `SceneExitFinalizationIntegrationTests`), `SessionMerger.cs:1221` source-tag log strings. Rewrote the "no change at binary codec" sentence to clarify "wire format unchanged; C# source expressions still rename."
  - **P0-4 (min-interval wiring gap)**: §3.1 only updated `effectiveMaxSampleInterval` for the debris tier; `effectiveMinSampleInterval` at `BackgroundRecorder.cs:1958-1960` was not extended. Consequence: at debris=100 m (Full tier), `effectiveMinSampleInterval` would fall through to the existing `proximityInterval` path, not `configuredMin` — the promised Full-cadence behaviour would only manifest through the max-interval backstop. Added an explicit row updating BOTH min and max wiring to take the new `debrisTier` parameter, and updated the §6.1 row for `FlightRecorder.cs:821-865` to extend both helpers in lockstep.
  - **P1-1 (test-side construction sites)**: enumerated three test-side `new RelativeAnchorResolverContext` sites (`RelativeAnchorResolverTests.cs:1959`, `ProductionAnchorWorldFrameResolverTests.cs:247`, `Harness/HarnessScenarios.cs:35`) and added a row for the `MakeContext` helper at `:1952` that needs a `focusRecordingId` parameter for the carve-out tests.
  - **P1-2 (binary version gates)**: added explicit note in §5.2 that ~20 `binaryVersion >= X` gates in `TrajectorySidecarBinary.cs` become constant-true under v13 and should be simplified to their true branches in the same commit (don't leave dead `else` branches behind).
  - **P1-3 (sentinel collision)**: switched the rejected-recording sentinel from `RecordingFormatVersion = 0` to `= -1`. A fresh `new Recording()` has `RecordingFormatVersion = 0` and a missing/corrupt value also parses to 0; the previous sentinel couldn't distinguish "rejected" from "legitimately zero." Updated §5.3 and §6.3 caller-side check.
  - **P1-4 (param-order mismatch)**: added explicit note in §3.7 that the existing `ResolveReFlyTreeCadenceSampleInterval` takes `(cadence, max, min)` but the shared module's `ResolveSampleInterval` takes `(tier, min, max)`. The forwarder must re-order arguments — easy to mis-wire on a "one-line forward."
  - **P1-5 (cascade depth note)**: added clarification in §3.4 that production `MaxRecordingGeneration = 1`, so the cascade scenario (T → F → G) is forward-looking. The §7.2 cascade test uses a synthetic fixture bypassing the cap. Chain math is correct if the cap is ever raised.
  - **P2-1 (IGhostPositioner doc citation)**: reconciled §3.7 site table (was citing `:78-80`) with §6.2 row (correctly cites `:65-77`) — the whole doc block above the signature needs rewrite, not just one line.
