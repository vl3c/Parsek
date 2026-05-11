# Absolute-primary debris contract with proximity-tiered cadence

**Status:** redraft (replaces v2 plan)
**Scope:** debris recordings only. No changes to Re-Fly merge/supersede flow, no changes to non-debris recorder/renderer dispatch, no changes to the `RelativeAnchorResolver` chain, no changes to the loop-anchor live-PID carve-out, no changes to `LegacyDebrisShadowGate`.
**Backwards compatibility:** none. Existing v12 recordings continue to render through the unchanged PR #803 shadow path inside their Relative sections (no degradation on existing saves), but new debris recordings produced after this change have a different shape (mostly Absolute) and the recorder no longer emits long-range Relative sections.

---

## 1. Why

Body-fixed (Absolute) data is the natural representation for debris. It has no anchor coupling, no lever-arm amplification, no shared-rotation channel that produces the synced-debris swing across multiple anchored pieces, and it renders correctly through the same standard path solo-flight vessels use. The only thing Relative-to-parent buys you in practice is sub-metre geometric accuracy in the close-formation window (common-mode noise cancellation, see `ghost-anchor-recording-chain-plan.md:81-83`) — useful for hard-attached / docking visuals where pixel-accurate spacing matters, irrelevant once debris drifts.

Today's debris contract (v12 / PR 3b) is the opposite: it writes Relative-to-parent for the entire debris lifetime regardless of distance. PR #803 then routed the renderer through the body-fixed shadow inside those Relative sections to fix the synced-swing artifact at long range. Body-fixed is **already** the load-bearing rendering surface for debris today — it's just stored as a shadow inside a Relative section instead of as the primary surface of an Absolute section.

This plan flips the roles:

- **Body-fixed (Absolute) becomes the primary recording surface, all distances, all the time.**
- **Sampling cadence scales with proximity to parent**, mirroring the Re-Fly tree tiers (`ReFlyTreeFullFidelityProximityRangeMeters = 250`, `ReFlyTreeHalfFidelityProximityRangeMeters = 500` at `FlightRecorder.cs:936-937`): full fidelity in 0–250 m, half in 250–500 m, normal adaptive beyond. Dense sampling does the load-bearing work for visual smoothness in proximity, replacing the geometric-accuracy mechanism Relative was providing.
- **Relative-to-parent data is recorded only inside 0–250 m**, as an *experimental* secondary track. Kept for a playtest cycle to evaluate whether anchor-coupled attitude micro-motion adds visible value over body-fixed-only rendering at close range. If playtest shows no benefit, it's dropped entirely in a follow-up.

The renderer always reads body-fixed data — from `frames` inside Absolute sections (the new majority case), and from `absoluteFrames` inside Relative sections (the experimental 0–250 m window, unchanged PR #803 shadow path).

## 2. What does NOT change

- `Source/Parsek/AnchorDetector.cs` — `ShouldUseRelativeFrame`, `RelativeFrameRangeLimit`, `IsRecordingAnchorEligible`. The `candidateRecording.IsDebris → false` exclusion at `:240` stays — new-contract debris remains ineligible as an anchor for non-debris vessels.
- `Source/Parsek/ParsekConfig.cs:42-46` — `RelativeFrame.EntryMeters` / `ExitMeters` (2300 / 2500 m). These are the non-debris thresholds and are not used by debris under this contract.
- `Source/Parsek/RelativeAnchorResolver.cs` — the recorded-anchor DAG resolver.
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal phases, ERS/ELS, `RecordingSupersedeRelation`.
- `Source/Parsek/LegacyDebrisShadowGate.cs` — v11 retroactive shadow-only fix for legacy broken saves.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged.
- Re-Fly tree sampling tiers (`ReFlyTreeFullFidelityProximityRangeMeters`, `ReFlyTreeHalfFidelityProximityRangeMeters`) for non-debris. They stay at 250 / 500 m.
- Format-v7 absolute-shadow recorder writes (`FlightRecorder.cs:8200-8205` and BG equivalent). New debris Relative sections inside the 0–250 m experimental window still write `absoluteFrames` alongside `frames`; that's where the renderer reads from inside Relative.
- `TryPositionFromRelativeAbsoluteShadow` (`ParsekFlight.cs:16717-16810`) — still used by the shadow path inside the experimental 0–250 m Relative window.
- `TryRouteAnchorRotationToShadow` (`GhostPlaybackEngine.cs:2928-3006`) — still the shadow router.

If a change touches a file outside the debris-specific list in §6, that's a scope violation; revisit the design.

## 3. The contract change

### 3.1 Recorder side: three concerns, one design

**Concern A — primary frame.** Debris always writes Absolute (body-fixed) sections, regardless of distance to parent. There is no "Relative for the whole lifetime" anymore.

**Concern B — sampling cadence tiers.** While in proximity of the parent, the BG recorder forces a tighter sample interval:

| Distance to parent | Cadence | Effective `maxSampleInterval` |
|---|---|---|
| 0–250 m | Full fidelity | configured `minSampleInterval` (clamped to ≤ configured `maxSampleInterval`) |
| 250–500 m | Half fidelity | 2 × configured `minSampleInterval` (clamped to ≤ configured `maxSampleInterval`) |
| 500 m+ | Normal adaptive | existing `ResolveDebrisAwareMaxSampleInterval` logic |

These mirror the Re-Fly tree tiers; reuse the `ReFlyTreeSamplingCadence { None, Half, Full }` enum and `ResolveReFlyTreeCadenceSampleInterval` helper (`FlightRecorder.cs:850-876`), or add a sibling `DebrisProximitySamplingCadence` enum if naming overlap is a problem.

The tiers are **proportional to the user's settings**: `configuredMin` and `configuredMax` come from `ParsekSettings.minSampleInterval` / `maxSampleInterval` (or `ParsekSettings.GetMaxSampleInterval(SamplingDensity)`), so a user on Low density gets a coarser tier ladder than a user on High density. Same proportionality model Re-Fly uses.

**Concern C — experimental Relative shadow inside 0–250 m.** While debris is within 250 m of the parent, the recorder *also* emits a Relative-to-parent section (existing v7 schema: `frames` = anchor-local offsets, `absoluteFrames` = body-fixed shadow). This is the optional / experimental track per §1 — it's the recorder-side artifact we want to see whether it adds visual value. Hysteresis: enter at 250 m, exit at 280 m (12 % band, playtest-tunable).

Outside the 0–250 m window: the recorder emits Absolute sections only, no Relative shadow.

When debris is inside 0–250 m and emitting both a Relative section (with its body-fixed `absoluteFrames` inside) and the Absolute primary surface... wait, that's two parallel sections. The existing TrackSection schema is non-overlapping. To avoid a schema change for the first pass, **we don't emit two parallel sections**. Instead:

- 0–250 m: emit a Relative section, exactly as today. `frames` = anchor-local. `absoluteFrames` = body-fixed.
  - The renderer reads `absoluteFrames` (body-fixed) — this is the primary path under the new framing.
  - `frames` (anchor-local) is the "experimental shadow" that nothing renders yet; kept on disk for evaluation.
- 250 m+: emit an Absolute section. `frames` = body-fixed.

This matches the schema today. The conceptual flip is in the *interpretation*: inside Relative sections, the renderer treats `absoluteFrames` as the primary surface (it already does for v12 debris, per PR #803), and `frames` is now the experimental layer.

If a future PR concludes the experimental layer has no value, the recorder can stop emitting Relative sections for debris entirely. At that point the schema simplification follows naturally: only Absolute sections, only body-fixed `frames`, no `absoluteFrames`.

**One debris-specific carve-out preserved from v12** (applies inside the 0–250 m Relative window): the anchor is unconditionally `treeRec.DebrisParentRecordingId`. The nearest-eligible candidate search is bypassed.

### 3.2 Renderer side

The renderer always consumes body-fixed data for debris:

- **Inside an Absolute section (250 m+, the new majority case)**: standard `InterpolateAndPosition` (body-fixed lookup via `body.GetWorldSurfacePosition`). Same path stable solo-flight vessels use. No anchor consulted. No tumbling-parent gate.
- **Inside a Relative section (0–250 m, experimental window)**: PR #803's shadow path via `TryRouteAnchorRotationToShadow`, reading `absoluteFrames`. Unchanged from today's v12 behaviour. The tumbling-parent gate continues to drive FX suppression. Legacy resolver path runs only if shadow doesn't cover; `Hidden` is the third-tier fallback.

The two renderer surgical guards from the previous draft remain necessary because the new contract introduces Absolute debris sections that didn't exist under v12:

1. **`DebrisRelativePlaybackPolicy.ShouldRetireFromDiagnostic` must not fire on `non-relative-section`** (`DebrisRelativePlaybackPolicy.cs:194-198`). Today it retires any parent-anchored debris in a non-Relative section. Drop `"non-relative-section"` from the retire list. The remaining four reasons stay.

2. **`ShouldEvaluateAnchorRotationReliability` must early-out for Absolute sections** (`ParsekFlight.cs:15131-15150`). Today it's recording-level and fires for every v12+ parent-anchored debris frame. Make section-aware: extend the predicate (or `TryEvaluateAnchorRotationReliability` at `:15152`) to receive `playbackUT`, look up the active section via `TrajectoryMath.FindTrackSectionForUT`, and return false (no evaluation) when the section is not Relative. Without this, the tumble gate would fire for the (now-irrelevant) parent's rotation while debris is in an Absolute section, push the route to `Hidden`, and disappear the debris.

### 3.3 `HighFidelityProximityRangeMeters` raise

`FlightRecorder.cs:935`: `HighFidelityProximityRangeMeters = 200.0` → **`250.0`**.

This is the parent-side density rule. It lives on the focused vessel's recorder; when split children are registered as proximity vessels (`ParsekFlight.cs:4880`), the parent keeps high-fidelity sampling active while any registered child is within this distance. Raising to 250 m matches the new debris-side full-fidelity tier (0–250 m), so both halves of the proximity pair sample at the configured minimum interval simultaneously.

When this constant is still actively used after this change:

| Scenario | Fires? | Notes |
|---|---|---|
| Focused vessel separates, debris child stays close | Yes | Parent keeps dense samples while debris is < 250 m |
| Focused vessel separates, both halves controllable | Yes | Parent dense while *any* registered split child is < 250 m, including controllable ones |
| Two vessels approach (docking, rendezvous, no shared split history) | No | No call to `RegisterHighFidelityProximityVessel` in those cases |
| Player switches focus off the parent after separation | No | The mechanism is per-recorder, only meaningful on the focused vessel's recorder. Parent goes BG. (Gap noted in §9.1.) |
| Debris drifts past 250 m | No | Proximity-active check returns false; parent hi-fi window expires |

The constant remains load-bearing for the parent half of the proximity pair when the parent stays focused. Symmetric with the new debris-side tiers on the BG recorder.

### 3.4 Visibility coupling

The v12 §7 contract said: *"Debris is bound to parent visibility — if parent recorded data is unresolvable at the requested UT, the debris doesn't render."*

Under this contract the rule changes:

- **Inside the 0–250 m experimental Relative window**: visibility coupling preserved. If the parent is unresolvable, the existing shadow path handles the failure cases (shadow covers → render from body-fixed shadow; doesn't cover → coverage retirement / Hidden). Identical to today.
- **Outside the experimental window (Absolute section)**: visibility coupling no longer applies. Debris in Absolute renders directly from body-fixed data; parent presence is irrelevant.

This is a **deliberate user-visible product change**. A drifted booster contrail continues to render along its recorded ballistic path even after the parent is destroyed / finalized / loop-anchored. CHANGELOG entry:

> Debris that has drifted out of parent-bubble proximity continues to render along its recorded ballistic path even after the parent recording is destroyed, finalized, or loop-anchored. Previously, all parent-anchored debris would vanish in this case.

### 3.5 Format version

**No format version bump.** On-disk schema is unchanged. The renderer treats v12 and post-change recordings identically:

- v12 recordings: only contain Relative debris sections (some long-range). Renderer's shadow path inside Relative continues to mask any synced-swing latent in the data exactly as today. No regression.
- Post-change recordings: short Relative sections (0–250 m experimental window) plus Absolute tails. Renderer's shadow path handles the Relative window; new Absolute path handles tails.

## 4. Frame transitions

Existing transition infrastructure handles Relative ↔ Absolute for non-debris; the same paths apply for debris.

### 4.1 Transition cases

| Trigger | Behaviour |
|---|---|
| Debris created at separation, distance to parent < 250 m | Open Relative-to-parent section (experimental shadow window). Cadence: full fidelity. |
| Debris created at separation, distance to parent ≥ 250 m (rare — debris usually born adjacent to parent) | Open Absolute section. Cadence: full or half fidelity depending on distance. |
| Debris in 0–250 m, drifts past 280 m | Close Relative, open Absolute. Cadence drops to half fidelity (250–500 m tier) or normal (500 m+). |
| Debris in Absolute, drifts back inside 250 m | Close Absolute, open Relative-to-parent. Cadence rises to full fidelity. |
| Debris crosses 500 m tier boundary | No section change. Cadence drops from half to normal adaptive (or vice versa on re-entry). |
| Parent goes on-rails | End debris recording (existing `CheckDebrisTTL` at `BackgroundRecorder.cs:1390-1404`, unchanged). |
| Parent destroyed mid-recording | If currently in Relative window: next sample's proximity check fails → close Relative, open Absolute. The recording continues in Absolute until debris destruction / on-rails / scene-exit (or `CheckDebrisTTL` ends it on the next tick if parent gone). |
| Debris goes on-rails | `BackgroundOnRailsState`, no TrackSection (existing). |
| Debris destroyed | End debris recording (existing). |
| Scene exit | Ballistic tail extrapolation writes Absolute (existing invariant). Unchanged. |

### 4.2 Hysteresis

The Relative→Absolute boundary (250 m enter / 280 m exit) has a small hysteresis band to prevent flapping. The 250 m / 500 m cadence-tier boundaries don't need hysteresis because they only change the sample interval, not section identity — flipping back and forth between full and half cadence has no on-disk artifact.

### 4.3 Boundary discontinuity

Frame transitions emit a boundary point at section close and section open. The recorder already handles this for non-debris (`SeedBoundaryPoint`, `CloseBackgroundTrackSection`, `StartBackgroundTrackSection`). Debris transitions reuse the same machinery. No new boundary code.

At the Relative→Absolute boundary, the last shadow-rendered position (from `absoluteFrames`) should equal the first body-fixed-rendered position (from new Absolute section's `frames`) within floating-point noise: both are authored from the same world position at the same UT. Verified with a unit test.

## 5. Why this shape (vs the v2 draft)

The v2 draft used the existing non-debris proximity thresholds (2300 m enter / 2500 m exit) to bound the Relative window and kept PR #803's always-shadow path inside it. Review surfaced that 2300 m is still long enough for the synced-swing failure mode to manifest if parent BG cadence is sparse — pre-engage drift can be 60-85 m even below the tumble-gate threshold, and the gate only fires above 150 °/s.

This redraft tightens the window to 0–250 m. At that scale, lever arm × any plausible slerp error is sub-metre. Combined with full-fidelity sampling on **both** sides of the pair (parent via `HighFidelityProximityRangeMeters = 250`; debris via the new 0–250 m tier), the input data has enough density that even the standard Relative resolver path would reconstruct correctly. The renderer continues to use the shadow path inside the window for safety, but the disk data is dense enough that the choice no longer carries a visible cost.

Beyond 250 m, the recorder doesn't emit Relative sections at all. There's no Relative data shape for the renderer to mishandle. The synced-swing failure mode is structurally prevented at the recorder.

The experimental Relative shadow inside 0–250 m is kept for one playtest cycle — purely to evaluate whether anchor-coupled attitude micro-motion (rendering through `frames` anchor-local) adds visible value over body-fixed-only rendering at close formation. If playtest shows no benefit, the recorder stops emitting Relative sections for debris in a follow-up, and `absoluteFrames` / `TryRouteAnchorRotationToShadow` becomes pure compatibility code for v12 recordings.

## 6. Concrete touchpoints

### 6.1 Recorder

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/BackgroundRecorder.cs` | `:4286-4303` (`UpdateBackgroundAnchorDetection`) | Replace the v12 unconditional Relative shortcut with a debris-specific tier-aware branch: compute distance to parent, evaluate cadence tier (`DebrisProximitySamplingCadence`), and gate Relative section emission on `distance < 250 m` (or `< 280 m` if currently in the Relative window). |
| `Source/Parsek/BackgroundRecorder.cs` | `:4600-4685` (`ApplyDebrisAnchorContractToState`) | Repurpose to `ApplyDebrisProximityRelativeMode` — same as today's body, but only invoked from within the 0–250 m window. Outside the window, the caller opens an Absolute section via existing helpers. |
| `Source/Parsek/BackgroundRecorder.cs` | `:3338-3460` (debris seed at `InitializeLoadedState`) | Initial seed: compute distance to parent. If `< 250 m`: seed Relative-to-parent (existing path). Else: seed Absolute. |
| `Source/Parsek/BackgroundRecorder.cs` | `:5269` (`ResolveDebrisAwareMaxSampleInterval`) | Extend to consult the new cadence tier. Implementation pattern follows `FlightRecorder.ResolveEffectiveMaxSampleInterval(ReFlyTreeSamplingCadence, ...)` at `:850-876`. |
| `Source/Parsek/BackgroundRecorder.cs` | new private state on `BackgroundVesselState` | Track current cadence tier (`debrisProximitySamplingCadence`) and the most recent distance-to-parent measurement. Mirrors `reFlyTreeSamplingProximityMeters` / `reFlyTreeSamplingProximitySource` on `FlightRecorder` (`:952-953`). |
| `Source/Parsek/FlightRecorder.cs` | `:935` | `HighFidelityProximityRangeMeters = 200.0` → `250.0`. |
| `Source/Parsek/DebrisRelativeRecorderPolicy.cs` | full file | Audit each method for the v12 all-Relative assumption. The policy now sees mixed Absolute (majority) + Relative (0–250 m) debris recordings. Most existing logic already walks both Relative coverage and non-Relative frames; verify the fallback paths handle Absolute-as-renderable-surface correctly. Rename `ShouldNormalizeParentAnchoredDebris` → `ShouldNormalizeDebrisRecordingTail` (optional, makes call sites self-documenting). |
| `Source/Parsek/RecordingFinalizationCacheApplier.cs` | `:119-180` | `TryGetLastAuthoredUT` for parent-anchored debris currently early-returns after Relative-only inspection. Update to consider Absolute sections too. |
| `Source/Parsek/RecordingStore.cs` | `:105-114` | No format version bump (per §3.5). |

### 6.2 Renderer

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | `:191-199` (`ShouldRetireFromDiagnostic`) | Drop `"non-relative-section"` from the retire reasons. Add inline comment: "new-contract debris in Absolute sections is not a coverage failure; the standard absolute renderer handles it." |
| `Source/Parsek/ParsekFlight.cs` | `:15131-15150` (`ShouldEvaluateAnchorRotationReliability`) | Make section-aware: predicate returns false for any section whose `referenceFrame != Relative`. Requires receiving `playbackUT` and looking up the active section via `TrajectoryMath.FindTrackSectionForUT`. |
| `Source/Parsek/ParsekFlight.cs` | `:15152` (`TryEvaluateAnchorRotationReliability`) | Plumb `playbackUT` into the section-aware predicate. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2796-2909` (`TryRouteAnchorRotationUnreliable`) | No semantic change. With the predicate fix, the router naturally returns `None` for debris in Absolute (predicate returns false → no evaluation), and the engine falls through to standard absolute dispatch. |
| `Source/Parsek/IGhostPositioner.cs` + `ParsekFlight.cs:16717-16810` (`TryPositionFromRelativeAbsoluteShadow`) | Unchanged. Still used by the shadow path inside the experimental 0–250 m Relative window. |
| `Source/Parsek/LegacyDebrisShadowGate.cs` | full file | Unchanged. |
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` other call sites | `ParsekFlight.cs:17905, 18078, 20129, 22085`; `GhostPlaybackEngine.cs:2717, 3133, 3160` | No code change at the call sites; the §6.2 row 1 fix propagates automatically. Inline comments noting the new retirement semantics. |

### 6.3 Settings / constants

| File | Change |
|---|---|
| `Source/Parsek/FlightRecorder.cs` | `HighFidelityProximityRangeMeters = 250.0` (was 200.0). |
| `Source/Parsek/FlightRecorder.cs` or `BackgroundRecorder.cs` | New constants `DebrisFullFidelityProximityRangeMeters = 250.0`, `DebrisHalfFidelityProximityRangeMeters = 500.0`, `DebrisRelativeShadowEnterMeters = 250.0`, `DebrisRelativeShadowExitMeters = 280.0`. (The first two mirror Re-Fly's tier constants; the latter two define the experimental shadow window with 12 % hysteresis, playtest-tunable.) |
| `Source/Parsek/ParsekConfig.cs` | No changes — debris does NOT use `RelativeFrame.EntryMeters` / `ExitMeters`. |
| `Source/Parsek/AnchorDetector.cs` | No changes to `ShouldUseRelativeFrame`. The `IsRecordingAnchorEligible` debris exclusion at `:240` stays. |

### 6.4 Format codec

| File | Change |
|---|---|
| `Source/Parsek/RecordingTreeRecordCodec.cs` | No change. No new fields, no schema change. |
| `Source/Parsek/RecordingPrecBinaryCodec.cs` | No change. |

## 7. Tests

### 7.1 Recorder unit tests (`Source/Parsek.Tests/`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_AtSeparation_Within250m_OpensRelative` | Debris created with parent in bubble, distance 50 m | First TrackSection is `Relative`, `anchorRecordingId == parent.RecordingId` |
| `Debris_AtSeparation_Beyond250m_OpensAbsolute` | Debris created, distance 300 m (rare edge case) | First TrackSection is `Absolute` |
| `Debris_DriftsPast280m_ClosesRelativeOpensAbsolute` | Debris recorded at 240 → 290 m | At first sample where distance > 280 m: Relative closes, Absolute opens |
| `Debris_ReentersWithin250m_ReopensRelative` | Debris at 300 m drifts back to 240 m | Absolute closes, Relative opens, anchor pinned to parent |
| `Debris_HysteresisProtectsAgainstFlapping` | Debris hovering at 255–275 m | No section flip across the hysteresis band |
| `Debris_CadenceTier_0to250m_UsesFullFidelity` | Debris at 100 m from parent | Effective `maxSampleInterval` == `configuredMin` |
| `Debris_CadenceTier_250to500m_UsesHalfFidelity` | Debris at 350 m from parent | Effective `maxSampleInterval` == `2 × configuredMin` (clamped to `configuredMax`) |
| `Debris_CadenceTier_Beyond500m_UsesNormalAdaptive` | Debris at 1500 m from parent | Effective `maxSampleInterval` falls through to existing adaptive logic |
| `Debris_CadenceTier_ProportionalToDensitySetting` | Debris at 100 m, density = Low vs High | Effective `configuredMin` scales with user setting |
| `Debris_AnchorIdentityAlwaysParent_InProximity` | Debris in 0–250 m with sibling debris closer than parent | Anchor remains parent (no nearest-search) |
| `Debris_ParentRecordingFinalized_TransitionsToAbsolute` | Parent recording finalized before debris UT | Debris recorder treats as out-of-proximity, writes Absolute |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection (existing) |
| `Debris_ParentDestroyedMidRelative_NextSampleClosesRelative` | Parent destroyed while debris is in Relative window | Next sample: close Relative, open Absolute |
| `Debris_BoundaryPointContinuity` | Debris crosses 280 m, Relative→Absolute | Last shadow-rendered position == first body-fixed-rendered position within 0.1 m |
| `DebrisRelativeRecorderPolicy_AbsoluteTail_LatestRenderableUTCorrect` | Debris with mixed Relative+Absolute sections | `TryGetLatestRenderableUT` returns the Absolute section's last frame UT |
| `RecordingFinalizationCacheApplier_AbsoluteOnlyDebris_LastAuthoredUTNonNaN` | Debris with Absolute-only tail (parent destroyed during proximity) | `TryGetLastAuthoredUT` returns the Absolute section's last frame UT, not NaN |

### 7.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_RelativeSection_NoGate_UsesShadowPath` | Debris in Relative (within 0–250 m), tumble gate inactive, shadow covers UT | Shadow positioner invoked; `route == ShadowPositioned` (`mode=always`) |
| `Debris_RelativeSection_GateFires_UsesShadowPath` | Debris in Relative, tumble gate fires, shadow covers | Shadow positioner invoked; `route == ShadowPositioned` (`mode=gated`); FX suppression set |
| `Debris_AbsoluteSection_StandardDispatch` | Debris in Absolute (drifted past 280 m) | `InterpolateAndPosition` invoked directly; no anchor lookup; gate predicate not evaluated |
| `Debris_AbsoluteSection_ParentTumbling_StillRenders` | Debris in Absolute, parent rotating at 200 °/s | Section-aware predicate returns false; route = `None`; standard absolute path runs; debris remains visible |
| `Debris_AbsoluteSection_ParentDestroyed_StillRenders` | Debris in Absolute, parent recording absent | Renders normally; visibility decoupling per §3.4 |
| `Debris_AbsoluteSection_NotRetiredByCoverageGate` | Debris in Absolute with `frames.Count > 0` | `ShouldRetireOutsideAuthoredRelativeCoverage` does NOT fire (regression test for §6.2 row 1) |
| `Debris_RelativeSection_ParentUnresolvable_ShadowFallback` | Debris in Relative, parent unresolvable, shadow covers | Shadow path renders; not retired |
| `LegacyV11Debris_StillUsesShadowGate` | v11 debris (no `DebrisParentRecordingId`) | `LegacyDebrisShadowGate` fires; route through shadow |
| `LegacyV12Debris_LongRangeRelative_StillUsesShadowPath` | v12 debris with long-range Relative section (legacy data) | Shadow path renders (no degradation on existing saves) |

### 7.3 Regression test for synced-debris bug

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_LongRange_Absolute_NoSyncedSwing` | Multiple debris pieces all anchored to same parent, all > 280 m from parent (so all in Absolute) | Per-frame position deltas independent of `parent.rot(t)`. No coordinated swing across pieces. |
| `Debris_CloseRange_Relative_ShadowPath_NoSyncedSwing` | Multiple debris pieces within 200 m, parent FG-recorded, full-fidelity samples | Shadow path renders from `absoluteFrames`. No shared parent-rotation channel. |
| `Debris_CloseRange_TumbleGate_ShadowPath` | Multiple debris within 200 m, parent tumbling at 200 °/s | Gate fires; shadow path engages (`mode=gated`); positions match `absoluteFrames` lerp |

### 7.4 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_DriftingAway_TransitionsToAbsolute` | Debris drift beyond 280 m | `[Recorder]` log shows `DEBRIS RELATIVE exit:` event; subsequent samples in Absolute section |
| `Debris_TumblingParent_ShadowPath_RemainsVisible` | Parent at 150 °/s, debris within 200 m | Gate fires; shadow path engages; debris visible and smooth |
| `Debris_ParentOnRails_DuringProximity_TtlEndsRecording` | Debris within 1000 m of parent; parent transitions to packed | `CheckDebrisTTL` fires; debris recording ends; no zombie sections |
| `Debris_PhysicsWarp_CadenceBoundary_NoArtifacts` | Debris hovering at 245-255 m during 2× physics warp | No flapping; cadence transitions occur cleanly |
| `Debris_AbsoluteSection_ParentSuperseded_ContinuesRendering` | Debris in Absolute; parent re-flown (superseded) | Debris ghost continues rendering through original Absolute section |
| `Debris_FullFidelityCadence_DensityProportionality` | Same debris at 100 m, two different density settings | Sample count scales proportionally to user's `minSampleInterval` |

### 7.5 Test fixture

Add a synthetic recording fixture in `Source/Parsek.Tests/Generators/`: parent recording with multiple debris pieces, drift through all three cadence tiers (0–250 / 250–500 / 500+), parent tumble window inside 0–250 m, parent on-rails event. Used by the regression tests in §7.3 and the contract-shape tests in §7.1.

## 8. Logging additions

| Log line | Subsystem | Trigger |
|---|---|---|
| `DEBRIS RELATIVE entry: dist=...m < 250m parent=...` | `BgRecorder` | Debris transitions into 0–250 m, opens Relative |
| `DEBRIS RELATIVE exit: dist=...m >= 280m parent=...` | `BgRecorder` | Debris drifts out of Relative window, opens Absolute |
| `DEBRIS cadence tier: tier=Full|Half|None dist=...m parent=...` (rate-limited) | `BgRecorder` | Per-section heartbeat showing the active cadence tier |

`GhostRenderTrace.cs` already emits `mode=gated|always` on the shadow-route line. Under the new contract:
- Debris in Relative section (0–250 m window): `mode=always` or `mode=gated`, same as today's v12 behaviour
- Debris in Absolute section (250 m+): no shadow-route line at all

A CI-runnable invariant test on a synthetic recording asserts these patterns directly.

## 9. Risks and decisions

### 9.1 Gap: parent BG cadence when focus shifts off parent

**Scenario**: player launches a rocket, separation produces debris, player switches focus to the payload (not the booster). Parent (booster) becomes BG-recorded. Debris is also BG. `HighFidelityProximityRangeMeters` is per-FlightRecorder-state; it no longer applies on the parent side.

**Mitigation**: the new debris-side cadence tier keeps the *debris* recording dense (0–250 m → full fidelity). On the parent side, BG cadence falls back to `ResolveDebrisAwareMaxSampleInterval` (or `maxSampleInterval` configured default). For body-fixed rendering of debris in Absolute sections this is fine — the renderer reads debris's own samples, not the parent's. Parent samples only matter inside the 0–250 m Relative window where the renderer reads `absoluteFrames` (still on the debris's own track), so even there parent cadence doesn't directly affect the rendered debris position.

The one residual case: the experimental `frames` (anchor-local) data inside the Relative window depends on the parent pose at sample time. If the parent is sparse BG, the recorder's `ApplyBackgroundRelativeOffsetForAnchorPose` resolves a sparse anchor pose. But this data only affects the *experimental layer*, not the rendered output. The rendered output reads `absoluteFrames` which is sampled at the debris-side density.

**Decision**: don't extend `RegisterHighFidelityProximityVessel` to BG parents in this PR. The deficit affects only the experimental layer, not user-visible rendering. If the experimental layer survives the playtest evaluation, revisit the BG-parent density mechanism as a follow-up.

### 9.2 Risk: existing v12 saves degrade after the change

**Scenario**: v12 recordings have long-range Relative debris sections.

**Mitigation**: the renderer's shadow path inside Relative is unchanged. v12 recordings continue to render through it exactly as today. **No regression on existing saves.** Post-change recordings have a different shape (Absolute majority, short Relative window) but the renderer handles both.

### 9.3 Risk: `DebrisRelativeRecorderPolicy` / `RecordingFinalizationCacheApplier` semantic shift

**Scenario**: both files were written assuming all-Relative debris.

**Mitigation**: §6.1 audit each method. Add unit tests covering v12-shape (all-Relative) and new-contract-shape (mixed Absolute+Relative, Absolute-only). Run regression suite plus new tests before merge.

### 9.4 Risk: experimental Relative shadow inside 0–250 m adds complexity without value

**Scenario**: the experimental `frames` anchor-local data inside Relative sections is written but the renderer reads `absoluteFrames`. The experimental layer costs storage and recorder cycles for no observable rendering benefit.

**Mitigation**: playtest evaluation. Specific check: in a controlled scenario (separation + tight-formation co-flight + tumble), compare visual quality with the experimental layer present vs absent (toggle via a debug setting). If observable difference is sub-perceptible, the follow-up PR drops Relative section emission for debris entirely and simplifies the schema.

### 9.5 Risk: scope creep into Re-Fly

**Mitigation**: §5 confirms the new constants are debris-specific (`DebrisFullFidelityProximityRangeMeters` etc), the threshold is independent (not 2300 m). Pre-merge audit per §10 step 5 — run `scripts/grep-audit-non-loop-live-pid.ps1` and `scripts/grep-audit-ers-els.ps1`, plus grep for `IsDebris && ... DebrisParentRecordingId` (≈ 33 hits).

### 9.6 Decision: 280 m exit hysteresis is a starting value

**Rationale**: 250 m enter / 280 m exit is 12 % hysteresis, in the same range as the existing non-debris (200 / 2500 m → 8.7 %) but proportional to the smaller window. Playtest may surface that 260 m or 300 m works better. The constants live in `Source/Parsek` and are one-line edits.

## 10. Rollout

1. Land the renderer guards (§6.2 rows 1-2: `ShouldRetireFromDiagnostic` and section-aware predicate) on a feature branch as commit 1. By themselves, no v12 behavioural change (v12 debris has no Absolute sections, so the guards are unreachable for existing data).
2. Land the `DebrisRelativeRecorderPolicy` / `RecordingFinalizationCacheApplier` updates (§6.1) as commit 2. Same: no effect until recorder produces mixed sections.
3. Land the recorder change — distance tiers, threshold, `HighFidelityProximityRangeMeters = 250` — as commit 3. New recordings start producing the new contract; the renderer guards from step 1 catch them correctly.
4. Land the test suite as commit 4 (§7).
5. Run `dotnet test` (full xUnit). Run Re-Fly in-game tests. Run `scripts/grep-audit-*.ps1`. Document grep audit of `IsDebris && DebrisParentRecordingId` (≈ 33 hits) in PR description.
6. Playtest:
   - Stage separation with multiple debris pieces; fly out past 500 m; observe section transitions and cadence drops.
   - Tumbling parent (gimbal-stuck booster) with debris inside 200 m. Verify shadow path engages.
   - Long ballistic arc with parent destroyed mid-flight. Debris continues in Absolute.
   - High-density vs Low-density user settings — confirm sample-count proportionality.
   - Toggle the experimental `frames` rendering path on a debug build; compare close-formation visuals. Decide whether to keep or drop the experimental layer in a follow-up.
7. Validate post-playtest: zero coverage-gate retire events on Absolute debris sections; `DEBRIS RELATIVE entry/exit:` events at threshold crossings; `mode=always|gated` lines only inside the 0–250 m window.
8. Update `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, `.claude/CLAUDE.md` (the format-version paragraph and the debris-shadow notes — all updates limited to debris contract behaviour).

## 11. Out of scope

- Any change to non-debris recorder or renderer dispatch.
- Format-version bump (per §3.5).
- Changing the loop-anchor live-PID carve-out for any vessel kind.
- Changing the `LegacyDebrisShadowGate` (v11 retroactive fix).
- BG-side equivalent of `RegisterHighFidelityProximityVessel` for BG parents (deferred per §9.1).
- Dropping the experimental Relative shadow inside 0–250 m. That's a follow-up after playtest evaluation (§9.4).
- Migrating existing v12 recordings (no migration needed; they continue to render via the preserved shadow path).
- Restoring v12's "debris bound to parent visibility" rule for the new Absolute case (deliberate change per §3.4).
- Recorder-side denser sampling for fast-debris-rotation cases (separate concern; affects attitude not position).

## 12. Appendix: predecessor PR chain

| PR | Branch | Did | Status under this plan |
|---|---|---|---|
| #793 | `fix-tumbling-parent-rot-interp` | Added angular-rate gate. Hide on tumble. | Gate retained. Section-aware predicate (`ShouldEvaluateAnchorRotationReliability`) extension fires gate only inside Relative sections. |
| #800 | `investigate-debris-smooth-trajectory` | Replaced hide with shadow lerp during tumble window. | Retained inside the 0–250 m Relative window. |
| #803 | `debris-always-shadow` | Made shadow unconditional for v12 debris inside Relative. | Retained inside the now-much-shorter (0–250 m) Relative window. |
| 3a | `Recording.DebrisParentRecordingId` schema | Field for parent identification. | Retained. Field still set; semantics unchanged. |
| 3b | `BackgroundRecorder` permanent-Relative debris contract | Always-Relative-to-parent for debris lifetime. | **Replaced** by absolute-primary + 0–250 m experimental shadow + tier cadence. "Anchor pinned to parent" preserved per §3.1. |
| 3c | `LegacyDebrisShadowGate` for v11 retroactive fix | Reads `absoluteFrames` for v11 broken saves. | Untouched. |
| PR #708 era | `pr708-playtest-followup-plan` (recorder hi-fi tiers) | `HighFidelityProximityRangeMeters = 200`, Re-Fly tree tiers 250/500. | `HighFidelityProximityRangeMeters` raised to 250 (§3.3) to match the new debris-side full-fidelity tier. Re-Fly tree tiers untouched. |

## 13. Plan evolution

This plan went through three drafts. The first (commit `4b9a9bd`) proposed proximity-gating at the existing 2300/2500 m threshold and reverting PR #803. Critical review surfaced that 2300 m is still long enough for the synced-swing failure mode to manifest at moderate parent BG cadence. The second draft (commit `257ff31`) tightened the renderer side: keep PR #803 inside the bounded Relative window, only guard against the new Absolute sections. User feedback then pivoted to the deeper structural change captured in this draft: absolute body-fixed becomes the primary recording surface always, with tiered cadence doing the load-bearing work for proximity rendering and the relative-anchor layer demoted to an experimental 0–250 m shadow that can be dropped after playtest evaluation. The `HighFidelityProximityRangeMeters` raise to 250 m makes both halves of the parent↔debris pair sample densely in the close-formation window.
