# Proximity-gated frame contract for debris

**Status:** revised draft (post-review)
**Scope:** debris recordings only. No changes to Re-Fly merge/supersede flow, no changes to non-debris recorder/renderer dispatch, no changes to the `RelativeAnchorResolver` chain, no changes to the loop-anchor live-PID carve-out, no changes to `LegacyDebrisShadowGate`.
**Backwards compatibility:** existing v12 recordings continue to render correctly because the renderer's always-shadow path inside Relative sections is preserved (see §3.2). Recordings produced after this change have shorter Relative sections and Absolute tails, but no on-disk schema change.

---

## 1. Why

Non-debris recordings already use the right rule for trajectory frame: open Relative when within proximity of an eligible anchor recording (entry 2300 m, exit 2500 m hysteresis — `Source/Parsek/ParsekConfig.cs:42-46`), close back to Absolute when out of proximity. This rule keeps Relative sections short and bounded, which is what makes the resolver chain robust for non-debris vessels.

Debris is the one exception. The v12 contract (PR 3b, `Source/Parsek/BackgroundRecorder.cs:4286-4303`) bypasses the proximity gate and writes Relative-to-parent for the **entire debris lifetime**, even when the debris drifts kilometres from parent. That created a degenerate input shape (long lever arm × shared parent rotation slerp = synchronised-debris swing across all anchored pieces of a separation event), which PR #803 then patched at the renderer by routing v12 debris through the absolute shadow unconditionally whenever shadow data covered the playback UT — bypassing the recorded Relative offsets entirely.

The renderer-side patch was the right fix for the failure mode it targeted. It is *not* the right place to fix the underlying problem: the recorder is producing data the renderer can't safely reconstruct from. A bounded recorder contract removes the failure-mode-producing data shape at the source. With short Relative sections, the lever arm is bounded, the parent is in proximity (so its samples are dense via the existing `highFidelityProximityVesselPids` mechanism), and the synced-rotation pathology is structurally prevented. Beyond the bubble, the debris records Absolute and renders independently — same contract non-debris already uses.

This plan also relaxes the v12 §7 "debris bound to parent visibility" rule for the long-range Absolute case: drifted debris continues to render along its body-fixed path even if the parent is destroyed / finalized / loop-anchored. A booster contrail 5 km from the rocket is visually meaningful regardless of whether the rocket subsequently exploded.

The plan keeps PR #803's renderer behaviour **inside** the Relative window unchanged. Inside the bubble, debris still routes through the absolute-shadow lerp via `TryRouteAnchorRotationToShadow`. The reasoning: even at 2500 m lever arm, BG-recorded parents can have moderate rotation rates (5–30 °/s steady drift) below the tumbling-parent gate threshold (150 °/s) but still above what slerp can faithfully reproduce against typical BG cadence. The geometric-accuracy gain Relative is supposed to deliver inside proximity windows depends on dense parent samples on both sides — which is reliable for FG-recorded non-debris peers but *not* reliable for BG-recorded debris-parent pairs. Keeping always-shadow inside Relative for debris pays the renderer cost we already pay today, and pays it only inside the proximity window.

What changes: the Relative section is now bounded. What stays: the renderer surface used inside that section.

## 2. What does NOT change

- `Source/Parsek/AnchorDetector.cs` — `ShouldUseRelativeFrame`, `RelativeFrameRangeLimit`, `IsRecordingAnchorEligible`. The `candidateRecording.IsDebris → false` exclusion at `:240` stays — v13 debris remains ineligible as an anchor for non-debris vessels.
- `Source/Parsek/ParsekConfig.cs:42-46` — `RelativeFrame.EntryMeters` / `ExitMeters` (2300 / 2500 m).
- `Source/Parsek/RelativeAnchorResolver.cs` — the recorded-anchor DAG resolver.
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal phases, ERS/ELS, `RecordingSupersedeRelation`. (`RewindInvoker.CopyInheritedIdentityForFork` at `:247` keeps propagating `DebrisParentRecordingId` to provisionals.)
- `Source/Parsek/LegacyDebrisShadowGate.cs` — v11 retroactive shadow-only fix for legacy broken saves.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged.
- Hi-fi sampling thresholds (200 / 250 / 500 m) for non-debris stay as documented in `pr708-playtest-followup-plan.md`.
- Format-v7 absolute-shadow recorder writes (`FlightRecorder.cs:8200-8205` and BG equivalent). New debris Relative sections still write `absoluteFrames` alongside `frames`; the shadow remains the rendering surface inside the Relative window per §3.2.
- `TryPositionFromRelativeAbsoluteShadow` (`ParsekFlight.cs:16717-16810`) — still used by the always-shadow path inside Relative.
- `TryRouteAnchorRotationToShadow` (`GhostPlaybackEngine.cs:2928-3006`) — still the shadow router.

If a change touches a file outside the debris-specific list in §6, that's a scope violation; revisit the design.

## 3. The contract change

### 3.1 Recorder side

**Old contract (v12 / PR 3b):** debris with `Recording.DebrisParentRecordingId != null` writes Relative-to-parent unconditionally for the entire recording lifetime. Proximity is not consulted. Section closes only on debris destruction, parent on-rails, parent destruction, scene exit, or going on rails (existing `CheckDebrisTTL` at `BackgroundRecorder.cs:1390-1404`).

**New contract:** debris with `Recording.DebrisParentRecordingId != null` writes Relative-to-parent **only while within proximity of the parent vessel**, using the same entry / exit hysteresis as non-debris (`AnchorDetector.ShouldUseRelativeFrame` reused as-is: 2300 m enter, 2500 m exit). When the debris drifts beyond the exit threshold, the recorder closes the Relative section and opens an Absolute section. If the debris later re-enters proximity (gravity slingshot, etc.), the Relative section reopens, again pinned to the parent.

One debris-specific carve-out preserved from v12:

**Anchor identity is pinned to the parent.** When the proximity gate says "Relative," the anchor is unconditionally `treeRec.DebrisParentRecordingId`. The `AnchorDetector.FindNearestRecordingAnchor` candidate-list / nearest-search remains bypassed for debris. This preserves the "no wrong-anchor" property: debris cannot accidentally pick a sibling debris piece or unrelated nearby vessel as its anchor. Because debris is excluded from `IsRecordingAnchorEligible`, sibling debris would not be picked anyway, but the pinning makes the contract explicit and avoids depending on the exclusion as a load-bearing rule.

**Distance source.** Distance is computed against the **parent vessel** specifically. When parent is loaded (`FlightRecorder.FindVesselByPid(parentRec.VesselPersistentId) != null && .loaded`), use parent's live world position. When parent is not loaded, the existing `CheckDebrisTTL` hook (`BackgroundRecorder.cs:1390-1404`) ends the debris recording within one tick — there is no cross-tick "use last known recorded pose" fallback to design. (See §4.1 row.)

### 3.2 Renderer side

**Inside a Relative section: unchanged from today.** v12+ parent-anchored debris (`IsDebris && DebrisParentRecordingId != null && LoopAnchorVesselId == 0u`) continues to render via `TryRouteAnchorRotationToShadow` whenever shadow covers the playback UT. PR #803's "always-shadow inside Relative" path stays. The tumbling-parent gate (`ShouldEvaluateAnchorRotationReliability`) continues to drive FX suppression. The shadow positioner returns `ShadowPositioned`; legacy resolver path runs only when shadow doesn't cover; `Hidden` is the third-tier fallback.

**Inside an Absolute section: standard non-debris dispatch.** The renderer dispatches v13 debris in Absolute through `InterpolateAndPosition` (body-fixed lookup). No anchor consulted. No tumbling-parent gate. Same path non-debris Absolute already uses.

The change at the renderer is therefore *not* a revert of PR #803. It is two narrow guards added so that the existing PR #803 path doesn't fire for the **new** Absolute sections that v13 debris recordings now contain:

1. **`DebrisRelativePlaybackPolicy.ShouldRetireFromDiagnostic` must not fire on `non-relative-section`.** Today (`Source/Parsek/DebrisRelativePlaybackPolicy.cs:194-198`), `non-relative-section` is one of four reasons that retire a parent-anchored debris ghost. Under v13 the predicate fires on every Absolute debris section and would silently hide the ghost. Drop `"non-relative-section"` from the retire list. The remaining three reasons (`parent-recording-id-empty`, `no-track-sections`, `no-covering-section`, `relative-and-shadow-frames-out-of-range`) keep their current semantics. Add an explicit unit test that exercises a v13 debris recording in an Absolute section with `frames.Count > 0` and asserts no retire fires.

2. **`ShouldEvaluateAnchorRotationReliability` must early-out for Absolute sections.** Today (`Source/Parsek/ParsekFlight.cs:15131-15150`) the predicate is recording-level (not section-level): it fires for every v12+ parent-anchored debris frame. Under v13, when the active section is Absolute, the predicate would still fire, the shadow positioner would fail (`TryRouteAnchorRotationToShadow` requires `section.referenceFrame == Relative`, `:2949-2954`), `fxSuppress` from a tumbling parent would push the route to `Hidden`, and the Absolute debris ghost would disappear purely because of the (now-irrelevant) parent's rotation. Make the predicate section-aware: extend `TryEvaluateAnchorRotationReliability` (`:15152`) to receive `playbackUT` and look up the active section via `TrajectoryMath.FindTrackSectionForUT`; return false (no evaluation) when the section is not Relative. Add explicit unit test `Debris_AbsoluteSection_ParentTumbling_StillRenders`.

Both changes are surgical conditionals on already-existing predicate functions. They do not touch the shadow positioner, the router, or the legacy v11 gate.

### 3.3 Visibility coupling

The v12 §7 contract said: *"Debris is bound to parent visibility — if parent recorded data is unresolvable at the requested UT, the debris doesn't render."*

After this change the rule splits:

- **Inside the proximity window (Relative section)**: visibility coupling preserved. If the parent is unresolvable at playback UT, the existing always-shadow path already handles the failure cases (shadow covers → render from body-fixed shadow data; shadow doesn't cover → coverage retirement / Hidden). User-visible behaviour identical to today.
- **Outside the proximity window (Absolute section)**: visibility coupling no longer applies. Debris in Absolute renders directly from body-fixed data; parent presence is irrelevant.

This is a **deliberate user-visible product change**, called out in the CHANGELOG: drifted debris continues to render along its recorded ballistic path even after parent destruction / finalization / loop-anchoring.

### 3.4 Format version

**No format version bump.** On-disk schema is unchanged. The renderer treats v12 and post-change recordings identically:

- v12 recordings: only contain Relative debris sections (some long-range, possibly with synced-rotation latent in the data). Renderer's always-shadow path inside Relative continues to mask the latent issue exactly as it does today. **No regression on existing saves.**
- Post-change recordings: contain shorter Relative debris sections (proximity-bounded) plus Absolute tails. Renderer's always-shadow path handles Relative; new Absolute path handles tails.

The format-version table (`Source/Parsek/RecordingStore.cs:105-114`) is not touched. Save-game compatibility is preserved without explicit branching.

## 4. Frame transitions

Existing transition infrastructure handles Relative ↔ Absolute for non-debris; the same paths apply for debris.

### 4.1 Transition cases

| Trigger | Behaviour |
|---|---|
| Debris created, parent in bubble (typical separation) | Compute distance to parent. If `< 2300 m`: open Relative-to-parent. Else open Absolute. (Most separations open Relative because debris is born adjacent to parent.) |
| Debris drifts past 2500 m from parent | Close Relative, open Absolute. (`AnchorDetector.ShouldUseRelativeFrame` returns false on hysteresis exit.) |
| Debris re-enters within 2300 m of parent | Close Absolute, open Relative-to-parent. |
| Parent goes on-rails | End debris recording (existing `CheckDebrisTTL` at `BackgroundRecorder.cs:1390-1404`, unchanged). The proximity gate's "parent not loaded" case never executes for more than one tick before TTL fires. |
| Parent destroyed mid-recording | If currently Relative: next sample's proximity check fails (parent vessel is null) → close Relative, open Absolute. The recording continues in Absolute until debris itself is destroyed / on-rails / scene-exit. (CheckDebrisTTL also ends the recording shortly after parent destruction — debris sees one or two Absolute samples before the recording ends. Acceptable.) |
| Debris goes on-rails | `BackgroundOnRailsState`, no TrackSection (existing). |
| Debris destroyed | End debris recording (existing). |
| Scene exit | Ballistic tail extrapolation writes Absolute (existing invariant). Unchanged. |

### 4.2 Boundary discontinuity

Frame transitions emit a boundary point at the section close + section open. The recorder already handles this for non-debris (`SeedBoundaryPoint`, `CloseBackgroundTrackSection`, `StartBackgroundTrackSection`). Debris transitions reuse the same machinery. No new boundary code.

For the playback side: at the Relative→Absolute boundary, the last shadow-rendered position should equal the first body-fixed-rendered position (within floating-point noise) because the shadow point at section-close and the absolute boundary point are both authored from the same world position at the same UT. Verify with a unit test that compares frame N (Relative + shadow render) and frame N+1 (Absolute, direct body lookup) within 0.1 m.

### 4.3 Hysteresis / flapping protection

The 2300 / 2500 m hysteresis (`AnchorDetector.RelativeFrameRangeLimit`) prevents flapping when debris hovers near the threshold. Same predicate used for non-debris; reuse without modification.

## 5. Why we keep PR #803's always-shadow inside Relative for debris

The original draft of this plan proposed reverting PR #803 entirely (use the standard `RelativeAnchorResolver` chain inside debris Relative sections). Review surfaced that this would re-expose a moderate-rotation BG-parent failure mode the proximity-gating doesn't fully prevent:

- Pre-engage drift (`debris-always-shadow.md` §"Symptom recap") was caused by sparse-parent samples at section close combining with parent rotation under the wide bracket span. Under proximity-gated v13, section close happens at the 2500 m hysteresis exit — typically when debris is far from focus and BG cadence is at its widest (3-8 s per sample on Medium/Low density settings). The thin-tail-at-section-end pattern is **at least as likely** under v13 as it was under v12.
- The tumble-gate threshold is 150 °/s — high enough that steady drift rotation (5-30 °/s) is below the gate but still above what slerp reproduces faithfully against 3-8 s parent brackets. PR #800 alone did not cover this; PR #803 did.
- Multiple debris pieces from a single separation event share the same parent rotation channel. The synced-error signature is visually obvious with as few as 3-4 pieces.

Keeping PR #803's always-shadow inside Relative for debris specifically pays the renderer cost we already pay today, and pays it only within ≤2500 m windows. The recorder-side proximity gate does the load-bearing fix (no long-range Relative sections to produce the data-shape pathology in the first place). The renderer keeps the safety net inside the bubble where parent BG cadence cannot be assumed dense.

For non-debris vessels in proximity (docking, formation flying), the standard `RelativeAnchorResolver` chain still runs as today. Two reasons it works there but not for debris:
1. Non-debris in proximity is typically FG-recorded (the player is on it or a peer is) — dense samples on both sides, no cadence pathology.
2. Non-debris in proximity is typically 1-2 vessels at a time (docking, hard-attached craft) at very short range (< 50 m) — synced-error magnitude is small, and there isn't a multi-piece "swing pattern" signature to amplify visibility.

Debris uniquely combines BG cadence + multi-piece anchored to one parent + lever arms up to 2500 m. The asymmetric renderer choice (always-shadow only for debris, only inside Relative) reflects this asymmetric input shape.

## 6. Concrete touchpoints

### 6.1 Recorder

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/BackgroundRecorder.cs` | `:4286-4303` (`UpdateBackgroundAnchorDetection`) | Replace the `if (DebrisParentRecordingId != null) → ApplyDebrisAnchorContractToState; return;` shortcut with a debris-aware proximity branch: compute distance to parent vessel, run `AnchorDetector.ShouldUseRelativeFrame(distance, isCurrentlyRelative)`, then either call the Relative-anchored helper or `ExitBackgroundRelativeMode` (existing) to flip to Absolute. |
| `Source/Parsek/BackgroundRecorder.cs` | `:4600-4685` (`ApplyDebrisAnchorContractToState`) | Repurpose to `ApplyDebrisProximityRelativeMode` — opens / maintains a Relative section anchored on parent (existing behaviour minus the unconditional invocation). The method's behaviour is unchanged when invoked; the change is the **caller-side gate** in `UpdateBackgroundAnchorDetection`. |
| `Source/Parsek/BackgroundRecorder.cs` | `:3338-3460` (debris seed at `InitializeLoadedState`) | Compute initial distance to parent. If `< 2300 m`: seed Relative-to-parent (existing path). Else: seed Absolute. |
| `Source/Parsek/DebrisRelativeRecorderPolicy.cs` | full file | The policy currently assumes a v12 parent-anchored debris recording is **all-Relative**. Under the new contract debris recordings are mixed Absolute+Relative. Audit each method for the assumption and update: <br>• `NormalizeRelativeTrackSections` (`:86-139`) — already iterates Relative sections only, fine. <br>• `TryGetLatestRenderableUT` (`:141-216`) — already walks both Relative coverage and non-Relative section frames, mostly fine; verify the fallback path treats Absolute sections as renderable surfaces. <br>• `TrimFlatPointsPastRenderableTail` (`:218-245`) — verify works correctly when latest renderable UT is in an Absolute tail (not just Relative). <br>• `ClampExplicitEndUT` (`:247-267`) — same verification. <br>• Rename `ShouldNormalizeParentAnchoredDebris` to `ShouldNormalizeDebrisRecordingTail` to reflect the broadened scope (the rename is optional but makes the call sites self-documenting). <br>Add unit tests for each method exercising a debris recording with an Absolute tail. |
| `Source/Parsek/RecordingFinalizationCacheApplier.cs` | `:119-180` | `TryGetLastAuthoredUT` for v12+ debris currently early-returns after looking only at Relative sections (`:157-180`). Update to consider Absolute sections too; `recording.Points` and `OrbitSegments` if applicable. |
| `Source/Parsek/FlightRecorder.cs` | `highFidelityProximityVesselPids` mechanism (`:93`, `:917`) | No change needed — split-child PIDs are already registered as hi-fi proximity targets, which keeps FG-parent samples dense whenever its child debris is close. Verify in playtest. |
| `Source/Parsek/RecordingStore.cs` | `:105-114` | No format version bump (per §3.4). |

### 6.2 Renderer

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | `:191-199` (`ShouldRetireFromDiagnostic`) | Drop `"non-relative-section"` from the retire reasons. Add inline comment: "v13 debris in Absolute sections is not a coverage failure; the standard absolute renderer handles it." |
| `Source/Parsek/ParsekFlight.cs` | `:15131-15150` (`ShouldEvaluateAnchorRotationReliability`) | Make section-aware: the predicate must return false for any section whose `referenceFrame != Relative`. This requires extending the predicate signature (or adding an inner sibling) to take `playbackUT` and look up the active section via `TrajectoryMath.FindTrackSectionForUT`. The PR #803 always-shadow gate then no longer fires for v13 debris in Absolute sections. |
| `Source/Parsek/ParsekFlight.cs` | `:15152` (`TryEvaluateAnchorRotationReliability`) | Plumb `playbackUT` into the section-aware predicate call. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2796-2909` (`TryRouteAnchorRotationUnreliable`) | No semantic change. With the predicate fix above, this router naturally returns `None` for v13 debris in Absolute (because `flags.tryEvaluateAnchorRotationReliability` returns false), and the engine falls through to the standard absolute dispatch. No router-level conditional needed. |
| `Source/Parsek/IGhostPositioner.cs` + `ParsekFlight.cs:16717-16810` (`TryPositionFromRelativeAbsoluteShadow`) | Unchanged. Still used by the always-shadow path inside Relative. |
| `Source/Parsek/LegacyDebrisShadowGate.cs` | full file | Unchanged. |
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` other call sites (`ParsekFlight.cs:17905, 18078, 20129, 22085`; `GhostPlaybackEngine.cs:2717, 3133, 3160`) | No code change at the call sites themselves; the §6.2 row 1 fix to `ShouldRetireFromDiagnostic` propagates automatically. Add inline comments at each call site noting the v13-aware retirement semantics. |

### 6.3 Settings / config

| File | Change |
|---|---|
| `Source/Parsek/ParsekConfig.cs` | No changes — debris reuses `RelativeFrame.EntryMeters` / `ExitMeters`. |
| `Source/Parsek/AnchorDetector.cs` | No changes to `ShouldUseRelativeFrame`. The `IsRecordingAnchorEligible` debris exclusion at `:240` stays (per §2). |

### 6.4 Format codec

| File | Change |
|---|---|
| `Source/Parsek/RecordingTreeRecordCodec.cs` | No change. No new fields, no schema change. |
| `Source/Parsek/RecordingPrecBinaryCodec.cs` | No change. |

## 7. Tests

### 7.1 Recorder unit tests (`Source/Parsek.Tests/`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_AtSeparation_OpensRelativeAnchoredOnParent` | Debris created with parent in bubble, distance 50 m | First TrackSection is `Relative`, `anchorRecordingId == parent.RecordingId` |
| `Debris_DriftsPastExitThreshold_ClosesRelativeOpensAbsolute` | Debris recorded at incrementing distances 2400 → 2600 m | At first sample where distance > 2500 m: Relative section closes, Absolute section opens |
| `Debris_ReentersWithin2300m_ReopensRelative` | Debris at 2700 m drifts back to 2200 m | Absolute section closes, Relative section opens, anchor pinned to parent |
| `Debris_HysteresisProtectsAgainstFlapping` | Debris hovering at 2350-2450 m | No section flip across the hysteresis band |
| `Debris_ParentRecordingFinalized_TransitionsToAbsolute` | Parent recording finalized before debris UT | Debris recorder treats as out-of-proximity, writes Absolute |
| `Debris_AnchorIdentityAlwaysParent` | Debris in bubble with sibling debris closer than parent | Anchor remains parent (no nearest-search) |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection (existing behaviour preserved) |
| `Debris_ParentDestroyedMidRelative_NextSampleClosesRelative` | Parent destroyed while debris is in Relative section | Next sample after destruction: close Relative, open Absolute |
| `Debris_BoundaryPointContinuity` | Debris transitions Relative→Absolute | Last shadow-rendered position equals first body-fixed-rendered position within 0.1 m |
| `DebrisRelativeRecorderPolicy_AbsoluteTail_LatestRenderableUTCorrect` | v13 debris with mixed Relative+Absolute sections | `TryGetLatestRenderableUT` returns the Absolute section's last frame UT |
| `RecordingFinalizationCacheApplier_AbsoluteOnlyDebris_LastAuthoredUTNonNaN` | v13 debris with Absolute-only tail (parent destroyed early) | `TryGetLastAuthoredUT` returns the Absolute section's last frame UT, not NaN |

### 7.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_RelativeSection_NoGate_UsesShadowPath` | v13 debris in Relative, tumble gate inactive, shadow covers UT | Shadow positioner invoked (PR #803 path); legacy resolver NOT invoked; `route == ShadowPositioned` (mode=always) |
| `Debris_RelativeSection_GateFires_UsesShadowPath` | v13 debris in Relative, tumble gate fires, shadow covers UT | Shadow positioner invoked; `route == ShadowPositioned` (mode=gated); FX suppression flag set |
| `Debris_RelativeSection_GateFires_NoShadow_RoutesHidden` | v13 debris in Relative, gate fires, no `absoluteFrames` | `route == Hidden`; mesh hidden |
| `Debris_AbsoluteSection_StandardDispatch` | v13 debris in Absolute (drifted past 2500 m) | `InterpolateAndPosition` invoked directly; no anchor lookup; gate predicate not evaluated |
| `Debris_AbsoluteSection_ParentTumbling_StillRenders` | v13 debris in Absolute, parent rotating at 200°/s | Section-aware predicate returns false; route = `None`; standard absolute path runs; debris remains visible |
| `Debris_AbsoluteSection_ParentDestroyed_StillRenders` | v13 debris in Absolute, parent recording absent | Renders normally; visibility coupling does not apply outside proximity |
| `Debris_AbsoluteSection_NotRetiredByCoverageGate` | v13 debris in Absolute with `frames.Count > 0` | `ShouldRetireOutsideAuthoredRelativeCoverage` does NOT fire (regression test for §6.2 row 1) |
| `Debris_RelativeSection_ParentUnresolvable_ShadowFallback` | v13 debris in Relative, parent unresolvable, shadow covers | Shadow path renders; not retired |
| `LegacyV11Debris_StillUsesShadowGate` | v11 debris (no `DebrisParentRecordingId`) | `LegacyDebrisShadowGate` fires; route through shadow (regression test for §2) |
| `LegacyV12Debris_LongRangeRelative_StillUsesShadowPath` | v12 debris with long-range Relative section (legacy data) | Shadow path renders (no degradation on existing saves) |

### 7.3 Regression test for synced-debris bug

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_LongRange_Absolute_NoSyncedSwing` | Multiple v13 debris pieces all anchored to same parent, all >2500 m from parent (so all in Absolute) | Per-frame position deltas are independent of `parent.rot(t)`. No coordinated swing across pieces. |
| `Debris_CloseRange_Relative_ShadowPath_NoSyncedSwing` | Multiple v13 debris pieces within 250 m of parent, parent FG-recorded | Shadow path renders all pieces from `absoluteFrames`. No shared parent-rotation channel. |
| `Debris_CloseRange_TumbleGate_ShadowPath` | Multiple v13 debris pieces close to parent that's tumbling at 200°/s | Gate fires; shadow path engages (mode=gated); positions match `absoluteFrames` lerp; no synced swing. |

### 7.4 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_DriftingAway_TransitionsToAbsolute` | Debris drift beyond 2500 m | `[Recorder]` log shows `RELATIVE exit:` event; subsequent samples in Absolute section |
| `Debris_TumblingParent_ShadowPath_RemainsVisible` | Parent rotating at >150°/s with debris in close proximity | Gate fires; shadow path engages; debris remains visible and smooth |
| `Debris_ParentOnRails_DuringProximity_TtlEndsRecording` | Debris within 1000 m of parent; parent transitions to packed | `CheckDebrisTTL` fires within one tick; debris recording ends with `ParentOnRailsOrDestroyed` reason; no zombie Relative section |
| `Debris_PhysicsWarp_BubbleEdge_GateBehavesCorrectly` | Debris hovering at 2400 m during 2× physics warp | No flapping; section transitions occur cleanly across warp tick boundary |
| `Debris_AbsoluteSection_ParentSuperseded_ContinuesRendering` | v13 debris in Absolute; parent re-flown (superseded) | Debris ghost continues to render through original Absolute section; supersede chain doesn't affect Absolute debris |

### 7.5 Test fixture

Add a synthetic recording fixture in `Source/Parsek.Tests/Generators/` reproducing the canonical scenarios: parent recording with multiple debris pieces, drift past hysteresis exit, parent tumble window, parent on-rails. Used by the regression tests in §7.3 and the v13-shape tests in §7.1/§7.2.

## 8. Logging additions

| Log line | Subsystem | Trigger |
|---|---|---|
| `DEBRIS RELATIVE entry: dist=...m < 2300m parent=...` | `BgRecorder` / `Recorder` | Debris transitions to Relative |
| `DEBRIS RELATIVE exit: dist=...m >= 2500m parent=...` | `BgRecorder` / `Recorder` | Debris transitions to Absolute |
| `DEBRIS proximity hold: dist=...m parent=...` (rate-limited) | `BgRecorder` | Per-section heartbeat for diagnostics |

`GhostRenderTrace.cs` already emits `mode=gated|always` on the shadow-route line. Under the new contract:
- v13 debris in Relative section → `mode=always` (steady-state) or `mode=gated` (tumble window) — same as today's v12 behaviour
- v13 debris in Absolute section → no shadow-route line at all (gate predicate returns false)

A CI-runnable invariant test on a synthetic recording asserts these patterns directly, replacing the brittle "grep playtest log for `mode=always`" approach.

## 9. Risks and decisions

### 9.1 Risk: parent BG cadence is sparse → shadow lerp is the safety net

**Scenario**: parent is BG-recorded, parent BG cadence at moderate distance is ~3-8 s. Debris is in Relative at 2000 m. Parent rotation at 5°/s.

**Mitigation**: the always-shadow path inside Relative (preserved from PR #803) renders from the recorder-authored `absoluteFrames`, which are sampled at the same cadence as the parent samples but contain the **debris's** body-fixed world position — not a parent-multiplied reconstruction. Chord error in the shadow lerp is bounded by the debris's own sample-to-sample motion, not by the parent's rotation × lever arm. **The parent BG cadence concern that existed for the standard `RelativeAnchorResolver` path does not apply to the shadow path.** This is a load-bearing reason for keeping PR #803's shadow path inside Relative.

### 9.2 Risk: existing v12 saves degrade after the change

**Scenario**: a player has v12 recordings on disk where debris stayed Relative for its entire lifetime, including at long range.

**Mitigation**: the renderer's always-shadow path inside Relative is unchanged. v12 recordings (which contain only Relative debris sections) continue to render via shadow exactly as they do today. **No regression on existing saves.** New post-change recordings will have Absolute tails that didn't exist in v12 data; existing saves don't have those sections, so the new Absolute renderer path is never invoked for v12 data.

### 9.3 Risk: `DebrisRelativePlaybackPolicy.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` becomes nearly unreachable

**Scenario**: under the new contract, debris Relative sections are short and parent stays in proximity throughout, so authored frame gaps within Relative sections are rare.

**Decision**: keep the policy as a defensive fallback. Cost is one if-check per frame; benefit is robustness against unexpected coverage gaps. Add a metric to playtest logs: "X firings per N debris frames" as a rollout-success criterion. If post-rollout telemetry shows zero hits over ≥10 hours of playtest, remove in a follow-up cleanup.

### 9.4 Risk: `DebrisRelativeRecorderPolicy` semantic shift introduces subtle bugs

**Scenario**: the policy was written assuming all-Relative debris. The §6.1 audit may miss an assumption.

**Mitigation**: the policy methods are pure-static, fully unit-testable. Add tests for each method exercising:
- v12 all-Relative debris (regression — current behaviour preserved)
- v13 mixed Absolute+Relative debris (new case)
- v13 Absolute-only debris (parent destroyed early)

Run the existing v12 regression suite plus the new v13 suite before merge.

### 9.5 Risk: scope creep into Re-Fly

**Mitigation**: every touchpoint listed in §6 is debris-specific. Pre-merge audit:
1. Run `scripts/grep-audit-non-loop-live-pid.ps1` and `scripts/grep-audit-ers-els.ps1` (existing CI gates).
2. Grep the codebase for `IsDebris && ... DebrisParentRecordingId` and `DebrisParentRecordingId != null`. Currently 33 hits in `Source/Parsek`. Each hit must be reviewed for whether it assumes the v12 always-Relative invariant; mark each reviewed hit with a comment.
3. Run all Re-Fly in-game tests and confirm pass.

### 9.6 Risk: visibility decoupling for Absolute debris (§3.3) is a product change

**Scenario**: under v12, debris vanished when parent recording became unresolvable. Under the new contract, debris in Absolute keeps rendering.

**Decision**: this is the intended product change. CHANGELOG entry will explicitly state: *"Debris that has drifted out of parent-bubble proximity continues to render along its recorded ballistic path even after the parent recording is destroyed, finalized, or loop-anchored. Previously, all parent-anchored debris would vanish in this case."*

If the user dislikes the new behaviour, a one-line revert (re-add a visibility-coupling check in the new Absolute dispatch path) is straightforward — the change is reversible.

## 10. Rollout

1. Land the renderer guards (§6.2 rows 1-2) on a feature branch as the **first** commit. These two surgical changes prevent the §3.2 regressions but, by themselves, don't change v12 behaviour because v12 debris recordings contain no Absolute sections — the new Absolute path is unreachable. This commit can be reviewed in isolation.
2. Land the `DebrisRelativeRecorderPolicy` and `RecordingFinalizationCacheApplier` updates (§6.1) as the **second** commit. Same as above: doesn't activate until the recorder produces mixed sections.
3. Land the recorder change (§6.1 first three rows) as the **third** commit. This is the moment new recordings start producing Absolute debris tails; the renderer guards from step 1 catch them correctly.
4. Run full xUnit suite. Confirm all Re-Fly in-game tests pass.
5. Run the §9.5 grep audit. Document the audit results in the PR description.
6. Playtest with the canonical scenarios:
   - Stage separation with multiple debris pieces, fly out to >2500 m, observe transition.
   - Tumbling parent (intentional gimbal-stuck booster) with debris in proximity.
   - Long ballistic debris arc into the next SOI (debris in Absolute well past parent destruction).
   - 4× physics warp during debris-near-bubble-edge.
7. Validate post-playtest: zero coverage-gate retire events on Absolute debris sections; expected `RELATIVE exit:` and `RELATIVE entry:` lines at threshold crossings; shadow-route lines only on Relative sections.
8. Update `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, `.claude/CLAUDE.md` (the format-version paragraph and the Phase D / debris-shadow notes) — all updates limited to debris contract behaviour.

## 11. Out of scope

- Any change to non-debris recorder or renderer dispatch.
- Format-version bump (per §3.4).
- Changing the loop-anchor live-PID carve-out for any vessel kind.
- Changing the `LegacyDebrisShadowGate` (v11 retroactive fix).
- Recorder-side denser sampling for fast-debris-rotation cases (separate concern; affects attitude not position).
- Hermite rotation interpolation for debris.
- Migrating existing v12 recordings (no migration needed — they continue to render correctly via the preserved always-shadow path).
- Restoring v12's "debris bound to parent visibility" rule for the new Absolute case (deliberate change per §3.3 / §9.6).

## 12. Appendix: predecessor PR chain

| PR | Branch | Did | Status under this plan |
|---|---|---|---|
| #793 | `fix-tumbling-parent-rot-interp` | Added angular-rate gate. Hide on tumble. | Gate retained. Section-aware predicate (`ShouldEvaluateAnchorRotationReliability`) extension fires gate only inside Relative sections. |
| #800 | `investigate-debris-smooth-trajectory` | Replaced hide with shadow lerp during tumble window. | Retained as the in-Relative path. |
| #803 | `debris-always-shadow` | Made shadow unconditional for v12 debris inside Relative. | **Retained inside Relative.** The "unconditional" property now applies inside the (much shorter) bounded Relative window only. New Absolute debris sections use standard dispatch. |
| 3a | `Recording.DebrisParentRecordingId` schema | Field for parent identification. | Retained. Field still set; semantics unchanged. |
| 3b | `BackgroundRecorder` permanent-Relative debris contract | Always-Relative-to-parent for debris lifetime. | **Replaced** by proximity-gated contract. The "anchor pinned to parent" property is preserved per §3.1. |
| 3c | `LegacyDebrisShadowGate` for v11 retroactive fix | Reads `absoluteFrames` for v11 broken saves. | Untouched. |

## 13. Review history

The original draft of this plan proposed reverting PR #803 entirely and using the standard `RelativeAnchorResolver` chain inside the new bounded Relative debris window. Critical review (`30e97c0..4b9a9bd`) surfaced three load-bearing issues that motivated the revised shape:

- **P0**: `DebrisRelativePlaybackPolicy.ShouldRetireFromDiagnostic` retires any non-Relative debris section. Under the original design v13 Absolute debris would silently disappear. Fixed in §6.2 row 1.
- **P0**: `ShouldEvaluateAnchorRotationReliability` is recording-level. Under the original design, a tumbling parent would push v13 Absolute debris to `Hidden` because the gate predicate fired regardless of section type. Fixed in §6.2 row 2 by making the predicate section-aware.
- **P0**: `DebrisRelativeRecorderPolicy` and `RecordingFinalizationCacheApplier` encode the v12 "all-Relative" invariant. The original draft missed both files entirely. Added in §6.1.

The cross-cutting insight from review was that reverting PR #803 reintroduces the moderate-rotation BG-parent failure mode the proximity-gating doesn't fully prevent (parent BG cadence at section close is wide; lever arm up to 2500 m; gate threshold 150 °/s misses 5-30 °/s drift). Keeping PR #803's shadow path inside the (now-bounded) Relative window pays the renderer cost we already pay today, eliminates the need for §9.1-style hi-fi-parent extensions, and preserves bit-identical rendering of existing v12 saves. This shape — **bounded recorder + unchanged renderer inside the bound** — is the load-bearing change of this plan.
