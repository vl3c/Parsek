# Proximity-gated frame contract for debris

**Status:** draft for review
**Scope:** debris recordings only. No changes to Re-Fly merge/supersede flow, no changes to non-debris recorder/renderer dispatch, no changes to the `RelativeAnchorResolver` chain.
**Backwards compatibility:** none. Existing v12 recordings with permanent-Relative debris sections may render with synced-rotation artifacts after this change; users re-record under the new contract. (Per project policy: "private development, fresh and correct.")

---

## 1. Why

Non-debris recordings already use the right rule for trajectory frame: open Relative when within proximity of an eligible anchor recording (entry 2300 m, exit 2500 m hysteresis — `ParsekConfig.cs:42-47`), close back to Absolute when out of proximity. This rule produces sub-metre geometric accuracy in the close-formation window (where it matters visually) and avoids the lever-arm-amplified slerp errors at long range.

Debris is the one exception. The v12 contract (PR 3b, `BackgroundRecorder.cs:4299-4302`) bypasses the proximity gate and writes Relative-to-parent for the **entire debris lifetime**, even when the debris drifts kilometres from parent. That created a degenerate input case (long lever arm × shared parent rotation slerp = synchronised-debris swing across all anchored pieces), which PR #803 then patched at the renderer by routing v12 debris through the absolute shadow unconditionally — bypassing the recorded Relative offsets entirely whenever shadow data covered the playback UT.

The renderer-side patch was correct for the failure mode it was fixing. But it gave away the geometric-accuracy win that Relative is *supposed* to produce inside the close-formation window — and it did so for every parent-anchored debris frame, not just the ones at long range. Re-Fly ghost chains do not have this problem because they use the standard proximity-gated contract: Relative only when close, Absolute when drifted. Debris should follow the same rule.

The PR #803 absolute-shadow render path is doing two jobs at once: (a) covering the long-range synced-swing bug that the always-Relative contract creates, and (b) covering the parent-tumble bug that PR #800 originally targeted. After this change, (a) goes away by construction (Relative is no longer written at long range), so the always-shadow path can be retired in favour of standard dispatch. The parent-tumble fallback (b) stays in scope as a render-side safety net inside the proximity window — see §5.

This plan ports debris onto the same recorder + renderer rules that already work for non-debris, with one debris-specific carve-out preserved: when debris is in Relative, the anchor identity is *pinned to the parent recording* (no nearest-eligible search). That preserves the v12 "no wrong-anchor" property that PR 3b/3c shipped to fix.

## 2. What does NOT change

This plan is debris-scoped. The following stay as-is:

- `AnchorDetector.ShouldUseRelativeFrame` and `RelativeFrameRangeLimit` — non-debris proximity gate.
- `ParsekConfig.RelativeFrame.EntryMeters` / `ExitMeters` — 2300 / 2500 m thresholds.
- `RelativeAnchorResolver` — the recorded-anchor DAG resolver.
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal phases, ERS/ELS, `RecordingSupersedeRelation`.
- `LegacyDebrisShadowGate` — v11 retroactive shadow-only fix for legacy broken saves stays untouched.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged.
- Hi-fi sampling thresholds (200 / 250 / 500 m) for non-debris stay as documented in `pr708-playtest-followup-plan.md`.
- Format-v7 absolute-shadow recorder writes (`FlightRecorder.cs:8200-8205` and BG equivalent). New debris Relative sections still write `absoluteFrames` alongside `frames`; the shadow remains a defensive fallback.

If a change touches a file outside the debris-specific list in §6, that's a scope violation; revisit the design.

## 3. The contract change

### 3.1 Recorder side

**Old contract (v12 / PR 3b):** debris with `Recording.DebrisParentRecordingId != null` writes Relative-to-parent unconditionally for the entire recording lifetime. Proximity is not consulted. Section closes only on debris destruction, parent on-rails, parent destruction, scene exit, or going on rails.

**New contract (v13 / this plan):** debris with `Recording.DebrisParentRecordingId != null` writes Relative-to-parent **only while within proximity of the parent vessel**, using the same entry / exit hysteresis as non-debris (2300 m enter, 2500 m exit). When the debris drifts beyond the exit threshold, the recorder closes the Relative section and opens an Absolute section. If the debris re-enters proximity later (rare but possible — gravity slingshot, etc.), the Relative section reopens, again pinned to the parent.

Two debris-specific carve-outs preserved from v12:

1. **Anchor identity is pinned to the parent.** The `AnchorDetector.FindNearestRecordingAnchor` candidate-list / nearest-search is still bypassed for debris. When the proximity gate says "Relative," the anchor is unconditionally `treeRec.DebrisParentRecordingId`. This preserves the "no wrong-anchor" property: debris cannot accidentally pick a sibling debris piece or unrelated nearby vessel as its anchor.

2. **Distance source.** Distance is computed against the **parent vessel** specifically, not against the nearest eligible anchor. Two cases:
   - **Parent is loaded** (`FlightRecorder.FindVesselByPid(parentRec.VesselPersistentId) != null && .loaded`): use the parent's live world position.
   - **Parent is not loaded** (parent went on-rails, or scene-loaded but not in the bubble): fall back to the parent's most recent recorded pose (resolved via the existing recorded-anchor lookup helper). If the recorded pose is stale (parent recording finalized before debris's UT), the recorder treats the parent as out-of-proximity and writes Absolute.

### 3.2 Renderer side

**Old dispatch (v12 + PR #803):** v12+ debris (`IsDebris && DebrisParentRecordingId != null`) renders via `absoluteFrames` shadow lerp unconditionally whenever the shadow covers the playback UT (`GhostPlaybackEngine.cs:2849-2854`, `TryRouteAnchorRotationToShadow` at `:2928-3006`). The recorded Relative offsets in `frames` are bypassed. Tumbling-parent gate evaluates per frame for FX-suppression but not for rendering.

**New dispatch (v13 / this plan):** debris uses standard playback dispatch — same path the engine already uses for non-debris.

- **Absolute section** → standard `InterpolateAndPosition` (body-fixed lookup). No anchor consulted. (Same as today's non-debris Absolute.)
- **Relative section** → standard `InterpolateAndPositionRelative` → `RelativeAnchorResolver` chain (recorded-data only, never live except in loop carve-out). (Same as today's non-debris Relative.)
- **Shadow fallback (PR #800-style)**: if the parent-tumble gate fires *and* the section is Relative *and* the shadow covers the UT, route through `TryPositionFromRelativeAbsoluteShadow`. This is the PR #800 design — shadow used only inside the gate window, not unconditionally. Outside the gate the Relative resolver runs.
- **Hide fallback**: if the gate fires AND shadow does not cover, `Hidden` route. Existing behaviour.
- **Coverage retirement** (`DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage`) stays — applies in either Absolute or Relative when neither `frames` nor `absoluteFrames` covers the playback UT.

This is conceptually a partial revert of PR #803 (drop the unconditional always-shadow path) while keeping PR #800's tumble-window shadow fallback. The tumbling-parent gate's per-frame evaluation continues to drive FX-suppression as today.

### 3.3 Visibility coupling

The v12 §7 contract said: *"Debris is bound to parent visibility — if parent recorded data is unresolvable at the requested UT (parent destroyed, parent finalized, parent rejected as loop anchor), the debris doesn't render."*

After this change the rule splits:

- **Inside the proximity window (Relative section)**: visibility coupling preserved. If the parent is unresolvable at playback UT, the Relative resolver returns false → debris ghost is hidden / retired by the existing path. (Identical to today's non-debris Relative behaviour.)
- **Outside the proximity window (Absolute section)**: visibility coupling no longer applies. Debris in Absolute renders directly from body-fixed data; parent presence is irrelevant. Debris that's drifted >2500 m from a parent that's since been destroyed / finalized / loop-anchored will continue to render along its recorded ballistic path.

Rationale: the original "bound to parent visibility" rule was a heuristic for the always-Relative contract, where debris was *meaningless* without parent context (it existed as part of the parent's visual narrative). Once debris drifts beyond the bubble, it's a piece of falling/orbiting hardware in its own right — a kilometres-away booster contrail is visually meaningful regardless of whether the parent is still around.

### 3.4 Format version

Bump to v13: `DebrisProximityGatedFrameFormatVersion = 13` in `RecordingStore.cs:105-114`. Replaces v12 as `CurrentRecordingFormatVersion`. Binary stamp follows the same convention (a no-op alias if no on-disk layout change is needed; a real alias if `TrackSection.endUT` semantics change for debris).

The format bump is for diagnostics and future-proofing — it does **not** drive a render-time conditional branch. Both v12 and v13 debris recordings dispatch through the same new playback path. Old v12 data with long-range Relative sections may render with synced-rotation artifacts; this is accepted (per "fresh, correct versions" policy).

## 4. Frame transitions

The recorder transitions between Relative and Absolute on the proximity-gate edge. Existing infrastructure handles this for non-debris; the same paths apply.

### 4.1 Transition cases

| Trigger | Old (v12) behaviour | New (v13) behaviour |
|---|---|---|
| Debris created, parent in bubble (typical separation) | Open Relative-to-parent immediately | Compute distance to parent. If `< 2300 m` (almost always true at separation): open Relative-to-parent. Else open Absolute. |
| Debris drifts past 2500 m from parent | Stay in Relative forever | Close Relative, open Absolute (`AnchorDetector.ShouldUseRelativeFrame` returns false on hysteresis exit). |
| Debris re-enters within 2300 m of parent | Stay in Relative (no transition) | Close Absolute, open Relative-to-parent. |
| Parent goes on-rails | End debris recording (existing `EndDebrisRecording` from `CheckDebrisTTL`) | Same — end debris recording. (Unchanged.) |
| Parent destroyed mid-recording | Continue Relative writes; resolver fallback at playback | If currently Relative: close Relative on next sample (parent unresolvable as proximity reference), open Absolute. If currently Absolute: continue Absolute. |
| Debris goes on-rails | `BackgroundOnRailsState`, no TrackSection (existing) | Same — no TrackSection while on rails. (Unchanged.) |
| Debris destroyed | End debris recording | Same. (Unchanged.) |
| Scene exit | Ballistic tail extrapolation writes Absolute (existing invariant) | Same — extrapolated tail is always Absolute, never Relative. (Unchanged.) |

### 4.2 Boundary discontinuity

Frame transitions emit a boundary point at the section close + section open. The recorder already handles this for non-debris (`SeedBoundaryPoint`, `CloseBackgroundTrackSection`, `StartBackgroundTrackSection`). Debris transitions reuse the same machinery; no new boundary code.

For the playback side, transitions between Absolute and Relative in non-debris are already smooth (no visible pop). Debris transitions inherit this.

### 4.3 Hysteresis / flapping protection

The 2300 / 2500 m hysteresis (`AnchorDetector.RelativeFrameRangeLimit`) prevents flapping when debris hovers near the threshold. Same predicate used for non-debris; reuse without modification.

## 5. The PR #800 fallback inside the proximity window

When debris is inside the proximity window (Relative section), the parent's reconstructed pose drives the rendered debris position. Fast parent rotation between samples = slerp can't reproduce the actual rotation curve = synced-rotation error scaled by lever arm.

At ≤ 2500 m lever arm and dense sampling (parent FG at full physics rate, BG with hi-fi proximity activation), the slerp error stays sub-degree and visual swing stays sub-metre. But playtest data showed parent-tumble events (>150°/s) where slerp is intrinsically wrong regardless of cadence. PR #800 covered this by routing through the shadow only when the angular-rate gate fired.

This plan keeps PR #800's tumble-gate shadow fallback. The renderer dispatch becomes:

```
if section is Relative AND tumble gate fires AND shadow covers UT:
    shadow positioner (PR #800 design)
elif section is Relative:
    relative resolver (standard non-debris dispatch)
elif section is Absolute:
    absolute positioner (standard non-debris dispatch)
elif gate fires AND no shadow:
    Hidden
```

Concretely in `GhostPlaybackEngine.cs:TryRouteAnchorRotationUnreliable` (`:2796-2909`):
- Tier 1 (`ShadowPositioned`): change condition. Today the tier fires whenever shadow covers (PR #803 unconditional). After the change: fires only when gate fires AND shadow covers. (PR #800 conditional.)
- Tier 2 (`None`): unchanged — fall through to standard dispatch.
- Tier 3 (`Hidden`): unchanged — gate fires AND no shadow.

The change is a single conditional flip in the router. The shadow positioner method (`TryPositionFromRelativeAbsoluteShadow` in `ParsekFlight.cs:16717-16810`) stays unchanged.

## 6. Concrete touchpoints

### 6.1 Recorder

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/BackgroundRecorder.cs` | `:4286-4303` (`UpdateBackgroundAnchorDetection`) | Replace the `if (DebrisParentRecordingId != null) → ApplyDebrisAnchorContractToState; return;` shortcut with a debris-aware branch that runs the proximity gate against the parent vessel. |
| `Source/Parsek/BackgroundRecorder.cs` | `:4600-4685` (`ApplyDebrisAnchorContractToState`) | Repurpose as `ApplyDebrisProximityRelativeMode` — only opens / maintains a Relative section anchored on parent. New sibling helper `ExitDebrisRelativeMode` closes Relative and opens Absolute on hysteresis exit. |
| `Source/Parsek/BackgroundRecorder.cs` | `:3338-3444` (debris seed at `InitializeLoadedState`) | Compute initial distance to parent. If within entry threshold → seed Relative-to-parent (existing path). Else → seed Absolute. |
| `Source/Parsek/FlightRecorder.cs` | `CreateBreakupChildRecording` callers | Verify FG-spawned debris also runs through the new gate. (Most debris is BG-recorded; FG path is the rarer case.) |
| `Source/Parsek/RecordingStore.cs` | `:105-114` | Add `internal const int DebrisProximityGatedFrameFormatVersion = 13;` and bump `CurrentRecordingFormatVersion` to it. |
| `Source/Parsek/RecordingStore.cs` | binary version constants | Add matching `DebrisProximityGatedFrameBinaryVersion = 13` no-op alias if no on-disk layout change. |

### 6.2 Renderer

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2796-2909` (`TryRouteAnchorRotationUnreliable`) | Tier 1 (`ShadowPositioned`) condition change: fire only when tumble gate fires AND shadow covers. Drop unconditional always-shadow for v12+ debris. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2928-3006` (`TryRouteAnchorRotationToShadow`) | No behavioural change to the helper itself; just called only on gate-fire now. |
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | `:79-97` (`ShouldSkipRecordedRelativeResolverForAuthoredFrameGap`) | Re-evaluate: under the new contract debris Relative sections shouldn't have authored frame gaps that need shadow bridging (parent is in proximity throughout the section's UT range by construction). Keep as a defensive fallback but expect it to fire rarely. |
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | `:65-77` (`ShouldRetireOutsideAuthoredRelativeCoverage`) | Unchanged. Retire-on-coverage-gap rule still applies for both Absolute and Relative. |
| `Source/Parsek/IGhostPositioner.cs` + impl | `TryPositionFromRelativeAbsoluteShadow` | Unchanged. Still used by the gated shadow path. |
| `Source/Parsek/LegacyDebrisShadowGate.cs` | full file | Unchanged. v11 legacy retroactive fix stays. |

### 6.3 Settings / config

| File | Change |
|---|---|
| `Source/Parsek/ParsekConfig.cs` | No changes — debris reuses `RelativeFrame.EntryMeters` / `ExitMeters` (2300 / 2500 m). |
| `Source/Parsek/AnchorDetector.cs` | No changes — `ShouldUseRelativeFrame` reused as-is. |

### 6.4 Format codec

| File | Change |
|---|---|
| `Source/Parsek/RecordingTreeRecordCodec.cs` | Bump version stamp. Sparse-write any new fields if added (none currently planned). |
| `Source/Parsek/RecordingPrecBinaryCodec.cs` | Same — version stamp only if no layout change. |

## 7. Tests

### 7.1 Recorder unit tests (`Source/Parsek.Tests/`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_AtSeparation_OpensRelativeAnchoredOnParent` | Debris created with parent in bubble, distance 50 m | First TrackSection is `Relative`, `anchorRecordingId == parent.RecordingId` |
| `Debris_DriftsPastExitThreshold_ClosesRelativeOpensAbsolute` | Debris recorded at incrementing distances 2400 → 2600 m | At first sample where distance > 2500 m: Relative section closes, Absolute section opens |
| `Debris_ReentersWithin2300m_ReopensRelative` | Debris at 2700 m drifts back to 2200 m | Absolute section closes, Relative section opens, anchor pinned to parent |
| `Debris_HysteresisProtectsAgainstFlapping` | Debris hovering at 2350-2450 m | No section flip across the hysteresis band |
| `Debris_ParentNotLoaded_FallsBackToRecordedPose` | Parent on-rails, debris loaded | Distance computed against parent's recorded pose; Relative if within threshold |
| `Debris_ParentRecordingFinalized_TransitionsToAbsolute` | Parent recording finalized before debris UT | Debris recorder treats as out-of-proximity, writes Absolute |
| `Debris_AnchorIdentityAlwaysParent` | Debris in bubble with sibling debris closer than parent | Anchor remains parent (no nearest-search) |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection (existing behaviour) |
| `Debris_ParentDestroyedMidRelative_NextSampleClosesRelative` | Parent destroyed while debris is in Relative section | Next sample after destruction: close Relative, open Absolute |

### 7.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_RelativeSection_NoGate_UsesResolverChain` | v13 debris in Relative, tumble gate inactive, parent dense samples | `RelativeAnchorResolver` invoked; shadow positioner NOT invoked; `route == None` |
| `Debris_RelativeSection_GateFires_ShadowCoverage_RoutesShadow` | v13 debris in Relative, tumble gate fires, shadow covers UT | Shadow positioner invoked; `route == ShadowPositioned`; FX suppression flag set |
| `Debris_RelativeSection_GateFires_NoShadow_RoutesHidden` | v13 debris in Relative, gate fires, no `absoluteFrames` | `route == Hidden`; mesh hidden |
| `Debris_AbsoluteSection_StandardDispatch` | v13 debris in Absolute (drifted past 2500 m) | `InterpolateAndPosition` invoked directly; no anchor lookup |
| `Debris_AbsoluteSection_ParentDestroyed_StillRenders` | v13 debris in Absolute, parent recording absent | Renders normally; visibility coupling does not apply outside proximity |
| `Debris_RelativeSection_ParentUnresolvable_Retires` | v13 debris in Relative, parent unresolvable | Resolver returns false; debris retired this frame |
| `LegacyV11Debris_StillUsesShadowGate` | v11 debris (no `DebrisParentRecordingId`) | `LegacyDebrisShadowGate` fires; route through shadow |

### 7.3 Regression test for synced-debris bug

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_LongRange_Absolute_NoSyncedSwing` | Multiple v13 debris pieces all anchored to same parent, all >2500 m from parent (so all in Absolute) | Per-frame position deltas of each piece are independent of `parent.rot(t)` slerp. No coordinated swing across pieces. |
| `Debris_CloseRange_Relative_NoSyncedSwing_DenseSamples` | Multiple v13 debris pieces within 250 m of parent, parent FG-recorded (dense samples) | Slerp error sub-degree → lever arm × error sub-metre → no visible synced swing. Position deltas match `expectedDM` ± floating-point noise. |
| `Debris_CloseRange_TumbleGate_RoutesShadow` | Multiple v13 debris pieces close to parent that's tumbling at 200°/s | Gate fires; all debris route through shadow; positions match `absoluteFrames` lerp; no synced swing. |

### 7.4 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_RelativeWindow_LooksAttached` | In-flight separation; debris pieces stay in Relative for first ~5 s while in proximity | Visual: pixel-accurate spacing during co-flight; no synced swing |
| `Debris_SeparationEvent_DriftingAway_TransitionsToAbsolute` | Debris drift beyond 2500 m | `[Recorder]` log shows `RELATIVE exit: dist=...` event; subsequent samples in Absolute section |
| `Debris_TumblingParent_ShadowFallback_RemainsVisible` | Parent rotating at >150°/s with debris in close proximity | Gate fires; shadow path engages (mode=gated); debris remains visible and smooth |

### 7.5 Test fixture

Add a synthetic recording fixture in `Source/Parsek.Tests/Generators/` reproducing the canonical scenario: parent recording with documented attitude curve (slow drift + tumble window) + multiple debris pieces at varying offsets + a drift-past-threshold transition. Used by the regression tests in §7.3.

## 8. Logging additions

| Log line | Subsystem | Trigger |
|---|---|---|
| `DEBRIS RELATIVE entry: dist=...m < 2300m parent=...` | `BgRecorder` / `Recorder` | Debris transitions to Relative |
| `DEBRIS RELATIVE exit: dist=...m >= 2500m parent=...` | `BgRecorder` / `Recorder` | Debris transitions to Absolute |
| `DEBRIS proximity hold: dist=...m parent=...` (rate-limited) | `BgRecorder` | Per-section heartbeat for diagnostics |

`GhostRenderTrace.cs` already emits `mode=gated|always` on the shadow-route line. After this change, `mode=always` should never appear for v13 debris (only `mode=gated`). This is checkable in playtest logs as a rollout sanity gate.

## 9. Risks and decisions

### 9.1 Risk: parent BG cadence is not dense enough → synced-swing returns inside proximity window

**Scenario**: parent is BG-recorded (player-focus moved to a different vessel), parent BG cadence is the standard ~0.22 s. Debris is in Relative at 2000 m. Even small parent rotation (5°/s) over a 0.22 s bracket = 1.1° slerp error × 2000 m = 38 m position swing per debris piece, synchronised across all pieces.

**Mitigation**: extend the existing `highFidelityProximityVesselPids` mechanism (currently FG-only at `FlightRecorder.cs:93`) to BG. When a debris recording is active and in Relative-to-parent, register the parent's PID for hi-fi BG sampling. This would force parent BG cadence dense whenever a debris child is in proximity. Implementation: a small companion HashSet on `BackgroundRecorder` and a check in `IsBackgroundHighFidelitySamplingActive`.

**Decision**: ship the proximity-gated contract first without this mitigation. The PR #800 tumble-gate shadow fallback covers high-rotation cases. If playtest shows visible synced-swing in the moderate-rotation BG-parent case, add the BG-side hi-fi mechanism as a follow-up. The contract change is reversible only with care, but the BG-cadence extension is a pure additive optimisation.

### 9.2 Risk: existing v12 saves render badly after the change

**Scenario**: a player has v12 recordings on disk where debris stayed Relative for its entire lifetime, including at long range. After this change those Relative sections render through the standard resolver chain (no more unconditional shadow), and the synced-swing artifact resurfaces.

**Decision**: accepted. Per project policy ("private development, fresh and correct versions") existing recordings are disposable. Players re-record under v13. The plan explicitly does not include a migration path or a v12-conditional render branch.

### 9.3 Risk: debris in Absolute outside proximity → visibility decoupled from parent → visual confusion

**Scenario**: debris drifted >2500 m from parent. Parent is then destroyed. Debris continues to render along its recorded ballistic path, even though "the parent it came from" is gone.

**Decision**: this is fine and arguably better than v12 behaviour. A booster contrail that's already 5 km away from the rocket is visually meaningful regardless of whether the rocket subsequently exploded. The original "bound to parent visibility" rule was a correctness heuristic for the always-Relative contract, not a product requirement.

### 9.4 Risk: `DebrisRelativePlaybackPolicy.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` becomes unreachable

**Scenario**: under the new contract, debris Relative sections are short (only span proximity windows), so authored frame gaps within Relative sections are rare. The policy might never fire.

**Decision**: keep the policy as a defensive fallback. Cost is one if-check per frame; benefit is robustness against unexpected coverage gaps. If post-rollout telemetry shows zero hits over a long playtest period, remove in a follow-up cleanup.

### 9.5 Risk: removing PR #803 always-shadow re-exposes a failure mode

**Scenario**: PR #803 fixed three issues (pre-engage drift, engage discontinuity, release discontinuity) by routing all v12 debris through shadow. After this change, only the tumble-window cases route through shadow.

**Re-analysis**:
- Pre-engage drift was caused by *thin-tail debris brackets at section ends* with parent rotating underneath. Under v13: Relative sections are now short (proximity-window-bounded), so section tails are not thin-tail-recorded — they're triggered by hysteresis exit, with normal cadence on both sides. The thin-tail failure mode should not re-emerge.
- Engage discontinuity was caused by gate firing between two debris-recording frames. Under v13: if the gate fires inside the proximity window, the renderer routes to shadow (PR #800 path) for the duration of the gate window. Same behaviour as PR #800 — engage discontinuity was already addressed there.
- Release discontinuity: same as engage, PR #800 path covers it.

**Decision**: the failure modes PR #803 enumerated were all consequences of the always-Relative contract producing data the renderer couldn't reconstruct cleanly. Under the proximity-gated contract, the input data shape is different — Relative sections are short, anchors are in proximity, optimiser has less room to thin out section tails. The PR #800 fallback covers the residual gate-window cases.

### 9.6 Risk: scope creep into Re-Fly

**Mitigation**: every touchpoint listed in §6 is debris-specific. The grep-audit gate (`scripts/grep-audit-non-loop-live-pid.ps1`) and the existing in-game Re-Fly tests will catch unintended Re-Fly regressions. Run both before merge.

## 10. Rollout

1. Land the recorder change first (§6.1) on a feature branch. Verify with new unit tests (§7.1).
2. Land the renderer change (§6.2) on the same branch. Verify with new unit tests (§7.2) and the regression suite (§7.3).
3. Bump format version (§6.4). Verify codec round-trip.
4. Run full xUnit suite. Run Re-Fly in-game tests as the gate against scope creep.
5. Playtest with the canonical scenarios:
   - Stage separation with multiple debris pieces, fly out to >2500 m, observe transition.
   - Tumbling parent (intentional gimbal-stuck booster) with debris in proximity.
   - Long ballistic debris arc into the next SOI (debris in Absolute well past parent destruction).
6. Validate post-playtest: zero `mode=always` shadow-route lines for v13 debris in the log; expected `RELATIVE exit:` and `RELATIVE entry:` lines at threshold crossings.
7. Update `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`, `.claude/CLAUDE.md` (the format-version paragraph and the Phase D / debris-shadow notes).

## 11. Out of scope

- Migration of existing v12 recordings (per §9.2).
- Extending `highFidelityProximityVesselPids` to BG parents of in-proximity debris (per §9.1) — follow-up if needed.
- Changing the loop-anchor live-PID carve-out for any vessel kind.
- Changing non-debris recorder or renderer dispatch.
- Changing the LegacyDebrisShadowGate (v11 retroactive fix).
- Recorder-side denser sampling for fast-debris-rotation cases (separate concern; affects attitude not position).
- Hermite rotation interpolation for debris.

## 12. Appendix: predecessor PR chain

| PR | Branch | Did | Status under this plan |
|---|---|---|---|
| #793 | `fix-tumbling-parent-rot-interp` | Added angular-rate gate. Hide on tumble. | Gate retained. Hide only when shadow unavailable. |
| #800 | `investigate-debris-smooth-trajectory` | Replaced hide with shadow lerp during tumble window. | Retained as the in-proximity tumble fallback. |
| #803 | `debris-always-shadow` | Made shadow unconditional for v12 debris. | Reverted: shadow only on gate-fire (back to PR #800 design). |
| 3a | `Recording.DebrisParentRecordingId` schema (v12) | Field for parent identification. | Retained. Field still set; semantics unchanged. |
| 3b | `BackgroundRecorder` permanent-Relative debris contract | Always-Relative-to-parent for debris lifetime. | Replaced by proximity-gated contract. |
| 3c | `LegacyDebrisShadowGate` for v11 retroactive fix | Reads `absoluteFrames` for v11 broken saves. | Untouched. |
