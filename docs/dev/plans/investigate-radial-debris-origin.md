# Investigation Plan: Radial Debris Origin Alignment

Date: 2026-05-09

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-investigate-radial-debris-origin`

Branch: `investigate-radial-debris-origin`

Base: `origin/main` / `fix-debris-ghost-initial-slide` tip at `85cf25ac` (`Fix debris review regressions`)

Retained log bundle: `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-09_0042_radial-booster-position-inexact`

Bundle capture commit: `1a274030` (`Clamp debris first visible frame to seed bridge end`). The investigation worktree is based on the later `85cf25ac`; relevant files changed between those commits (`FlightRecorder.cs`, `GhostPlaybackEngine.cs`, `ParsekFlight.cs`, `RelativeAnchorResolver.cs`, and tests), so implementation must recheck the exact current-HEAD contracts before changing behavior. The seed, appearance, and Relative-frame conclusions below were rechecked against `85cf25ac`.

## Problem

After PR #776 fixed the visible initial debris slide, radial-attached side booster debris still appears a few meters too far forward or otherwise misaligned from the decoupler / booster attachment position.

The old failure mode was that the first visible frames fell back to a single absolute point or absolute shadow before the Relative section became usable. The retained bundle shows that path is now guarded: the first visible frames for the radial boosters are Relative and `firstFrameClamped=T`. The remaining symptom is therefore a reference-point mismatch, a seed/sample timing artifact, or a ghost visual root transform issue.

## Concrete Evidence

All four concrete cases use `radialDecoupler1-2` as the ghost craft root part. In the ghost craft snapshots, `root = 0`, `PART[0]` is the radial decoupler at local position `(0,0,0)`, and the booster tank is a child of the decoupler with a local offset around `(-0.789,-0.098,0)`. That means the rendered visual transform is rooted on the decoupler origin, not the booster body origin or a surface contact point.

| recording | root part | section metadata | seed frame | next ordinary frame | appearance log evidence |
| --- | --- | --- | --- | --- | --- |
| `61573dc3` | `radialDecoupler1-2`, PID `2057942744` | `sectionAuthoritative=True`, Relative, anchor `8a1f9ec8` | UT `226.54518554690023`, local `(2.679,-77.178,-0.567)`, flags `1` | UT `227.06518554690049`, local `(1.730,-10.280,-0.998)` | `KSP.log:13428`: `firstFrameClamped=T`, `activeFrame=Relative`, root `(-958.29,-6.26,-19.64)`, bounds-root `(-1.28,-0.13,-1.57)`, root part world equals ghost root |
| `5679c491` | `radialDecoupler1-2`, PID `1009856088` | `sectionAuthoritative=True`, Relative, anchor `8a1f9ec8` | UT `226.54518554690023`, local `(0.557,-77.148,0.658)`, flags `1` | UT `227.06518554690049`, local `(-1.689,-10.244,0.974)` | `KSP.log:13439`: `firstFrameClamped=T`, `activeFrame=Relative`, root `(-959.79,-4.25,-16.59)`, bounds-root `(-1.97,0.08,-0.16)`, root part world equals ghost root |
| `f44b52af` | `radialDecoupler1-2`, PID `3027027466` | `sectionAuthoritative=False`, Relative, anchor `8a1f9ec8`; flat points start at UT `230.105` | UT `229.58518554690178`, local `(-6.952,-92.939,0.670)`, flags `1` | UT `230.10518554690205`, local `(0.956,-8.814,0.753)` | `KSP.log:24850`: `firstFrameClamped=T`, `activeFrame=Relative`, root `(-1403.27,-8.18,-267.87)`, bounds-root `(-1.45,0.41,-1.42)`. Nearby `UpdatePath` logs show `mode=RecordedRelative` using TrackSection frames. |
| `cf08fb37` | `radialDecoupler1-2`, PID `2130796824` | `sectionAuthoritative=False`, Relative, anchor `8a1f9ec8`; flat points start at UT `230.105` | UT `229.58518554690178`, local `(-9.112,-92.944,-0.577)`, flags `1` | UT `230.10518554690205`, local `(-1.703,-8.788,-0.787)` | `KSP.log:24860`: `firstFrameClamped=T`, `activeFrame=Relative`, root `(-1404.51,-9.69,-265.51)`, bounds-root `(-1.99,-0.43,-0.41)`, root part world equals ghost root |

Additional retained-log evidence:

- `KSP.log:11908`, `11938`, `21978`, `22007`, `27361`, and `27391` show `initial activation hidden: reason=debris-seed-bridge`, confirming PR #776's hide/clamp path is active.
- The first visible appearances are already past the hidden seed bridge. For example, `61573dc3` appears from the ordinary frame at UT `227.065`, and `f44b52af` appears from the ordinary frame at UT `230.105`. The remaining visible misalignment must therefore be explained against the first ordinary frame/reference point, not only against the hidden seed.
- For `f44b52af`, `KSP.log:24845-24848` shows actual playback used the TrackSection Relative frame: `FrameStart ... ref=Relative`, `RelativeResolver ... localOffset=(0.96,-8.81,0.75)`, then `UpdatePath ... mode=RecordedRelative`.
- For `f44b52af` and `cf08fb37`, the appearance diagnostic line prints `recordingStart@230.11 frame=Relative offset=(-0.10,-74.46,1073.63)` or similar. That value is the top-level flat point interpreted through section metadata, not the first TrackSection Relative frame. This is a debug logging problem, not evidence that playback used flat `Recording.Points`.

## Relevant Code Paths

- `Source/Parsek/FlightRecorder.cs`
  - `TryConsumePendingJointChildPartOriginSeed` consumes a cached joint-child seed by part PID.
  - `TryCreateAbsoluteTrajectoryPointFromPartOrigin` samples `part.transform.position` and `part.transform.rotation`.
  - `OnPartJointBreak` captures `joint.Child` part-origin seeds for structural joints when `joint == joint.Child.attachJoint`.
- `Source/Parsek/ParsekFlight.cs`
  - `TrySelectDecouplePartOriginSeed` consumes the joint-child seed only when the new vessel root PID matches.
  - `OnDecoupleNewVesselDuringSplitCheck` stores the consumed seed as `joint-child-part-origin`; otherwise it samples the new vessel root part.
  - `ProcessBreakupEvent` passes the pre-captured trajectory point into background recording.
  - Relative playback resolution uses TrackSections first and does not fall through to flat points for the concrete appearance frames.
- `Source/Parsek/BackgroundRecorder.cs`
  - `CreateAbsoluteTrajectoryPointFromVessel(... preferRootPartSurfacePose: true)` resolves the root part surface pose for explicit split/fallback samples that request it.
  - Loaded-background physics samples currently call `CreateAbsoluteTrajectoryPointFromVessel(bgVessel, ut, currentVelocity)` without `preferRootPartSurfacePose`, so ordinary samples use vessel lat/lon/alt and `srfRelRotation` unless a future corrected/root-pose wrapper routes them differently.
  - `InitializeLoadedState` converts the initial absolute seed into a parent-relative offset using the live parent pose at initialization time.
  - Loaded-background sampling keeps an `absolutePoint` before Relative conversion and passes it as `TrackSection.absoluteFrames`, so any correction must keep `Recording.Points`, `TrackSection.frames`, and `TrackSection.absoluteFrames` consistent.
  - `ApplyBackgroundRelativeOffsetForAnchorPose` writes Relative local metre offsets into `TrajectoryPoint.latitude`, `longitude`, and `altitude`.
- `Source/Parsek/GhostVisualBuilder.cs`
  - Snapshot root part info is read from the ghost craft `root` index and part-local data.
  - The retained snapshots put the radial decoupler at root local `(0,0,0)`, with the booster as a child.
- `Source/Parsek/GhostPlaybackEngine.cs`
  - `TrackGhostAppearance` validates first visible frame state.
  - `DescribeAppearanceRecordingStartPoint` can mix top-level flat points with section labels for non-authoritative section recordings.

## Answers To Investigation Questions

### 1. What does PR #776 seed capture for radial decouplers?

The active structural seed path captures `joint.Child` part origin, not the joint/contact point, not the booster subtree root, and not the vessel COM. Explicit split/fallback samples that request `preferRootPartSurfacePose` also align to the new vessel root part. In the retained recordings, that root part is the radial decoupler.

Ordinary loaded-background samples are less certain: current code samples vessel pose unless a caller explicitly asks for root-part pose. Phase 1 diagnostics must therefore compare vessel pose, root-part pose, contact candidates, and the first ordinary absolute shadow before choosing the correction.

The routing invariant is also unproven by retained recording-time logs: `TrySelectDecouplePartOriginSeed` only consumes the pending seed when `joint.Child.persistentId == newVessel.rootPart.persistentId`. Code comments describe this as the stock KSP decoupler split behavior, but Phase 1 must log those PIDs side by side at the same split.

### 2. What is the KSP event sequence for these radial decouplers?

The retained playback bundle does not include the original recording-time joint-break logs, so it cannot prove `joint.Parent`, `joint.Host`, and `newVessel.rootPart` directly at the moment of break. The saved artifacts prove the resulting ghost craft root is the radial decoupler and the booster tank is a child of that root. Existing code treats `joint.Child` as the separated structural child when `joint.Child.attachJoint == joint`.

The missing piece is bounded recording-time diagnostics for `joint.Child`, `joint.Parent`, `joint.Host`, `joint.Child.parent`, `newVessel.rootPart`, and retained subtree membership.

### 3. What should the desired seed align?

It should not align COM. COM alignment would break the snapshot-root transform contract and make rotations wrong.

The desired contract needs a product decision:

- If the goal is exact replay of KSP's new vessel root, current root-part-origin sampling is internally consistent.
- If the goal is visual alignment at the separation interface, the seed should align a contact/attach point, then convert that contact back into the root-part world pose by subtracting the root-local contact offset.
- If the goal is booster-body alignment, the seed should align the booster subtree root or a booster contact node, again converted back into the radial-decoupler root pose before writing the trajectory point.

For the observed symptom, the best candidate is the booster/decoupler attach contact, not the decoupler origin and not COM.

### 4. Is this recorder data, ghost visual root transform, or seed bridge artifact?

This is most likely a recorder reference-point / first-ordinary-frame issue plus a separate hidden seed bridge artifact.

The ghost visual root transform looks correct for the data it receives: the ghost root is the snapshot root part, the root part is `radialDecoupler1-2` at local `(0,0,0)`, and appearance logs show root part world equals ghost root. Actual playback also uses TrackSection Relative frames for the concrete appearances.

The huge seed-to-next-frame delta is a seed bridge / parent-pose timing artifact: the seed is at the split UT, but the relative conversion appears to use a later live parent pose during `BackgroundRecorder.InitializeLoadedState`. PR #776 hides/clamps that bridge, so it no longer appears as the old initial slide.

The remaining few-meter misalignment is visible on the first ordinary frame after the hidden bridge. The leading explanations are:

1. the first ordinary frame is already physically separated from the decoupler/contact because visibility is clamped until the later sample; or
2. the ordinary frame's recorded reference point is not the semantic point users expect to align, such as the booster/decoupler contact.

Both explanations need the same next evidence: record root, vessel, contact, and absolute-shadow poses at the first ordinary frame.

### 5. Does undocking share the same problem?

Treat undocking as a separate branch. The undock path shares the low-level part-origin seed helper only when the undocked part is the new root, but docking-port undock does not have the same radial decoupler plus booster-subtree geometry. No evidence in this bundle says undocking needs the radial-contact correction.

## Root Cause Hypothesis

Confidence: medium, about 65%.

The recorder definitely writes a structural seed at the new debris root part origin. For radial booster debris, that root is the retained radial decoupler, while the visible object users mentally align against is probably the booster/decoupler contact or the booster body. The remaining uncertainty is whether the first visible ordinary frame is misaligned because of this reference-point choice, because it is already a later post-separation sample, or both.

Two secondary observations affect implementation:

1. The first Relative seed in the sidecar is very far from the next ordinary Relative sample because it is converted against a different parent pose time. That is important for bridge behavior, but PR #776 prevents it from being directly visible.
2. The `GhostAppearance` diagnostic can label top-level flat points as Relative for non-authoritative sections. That can mislead investigation, but does not appear to drive playback.
3. Ordinary background physics samples are not proven to be root-part-origin samples in current code. The next plan step must log and compare vessel pose, root-part pose, and absolute shadow data at the first visible ordinary frame.

## Narrowest Safe Fix Surface

Do not start with a broad render-side correction. The narrowest safe next step is diagnostics, then a recorder-side reference-point correction if diagnostics confirm the contact-point hypothesis.

### Phase 1: Add bounded diagnostics only

Add one-shot or rate-limited logs around the split capture path. These logs should use the normal `[Parsek][LEVEL][Subsystem]` format and should not log per part in large loops.

Use `ParsekLog.Verbose` for one-shot break/init diagnostics and `ParsekLog.VerboseRateLimited` only where sampling can repeat. Numeric formatting must use `CultureInfo.InvariantCulture`. Phase 1 must capture both the live break-time contact world position and values reconstructable later from snapshot/root/`srfAttachNode` data, because that decides whether Phase 4 can recompute the correction or must persist a recording field.

#### `FlightRecorder.OnPartJointBreak`

Log for structural joint breaks:

- `joint.Child`, `joint.Parent`, `joint.Host`
- `joint.Child.parent`
- `joint.Child.attachJoint == joint`
- the non-structural / `joint.Child.attachJoint != joint` path too, because the retained bundle lacks recording-time evidence and that precondition currently decides whether the part-origin seed path runs
- child PID/name and vessel/root PID/name
- child PID next to the eventual new vessel root PID when the split callback consumes the pending seed, so the stock-KSP root-at-child invariant is visible in one log sequence
- child origin world position and rotation
- child `srfAttachNode` local position, world position, and orientation when present
- candidate joint/contact world position if `PartJoint` exposes a reliable anchor or transform
- chosen pending seed source and seed world position

`AppendStructuralEventSnapshot` sits near this path but records a parent-recording structural-event snapshot at the joint-break UT for the pre-split shared vessel. It is not the new debris trajectory seed and is not a likely correction surface for radial debris origin alignment.

#### `ParsekFlight.OnDecoupleNewVesselDuringSplitCheck`

Log when a new split vessel is accepted:

- new vessel id/name and root part PID/name
- pending joint child PID/name beside the new vessel root PID/name when available
- new vessel root origin, vessel COM, and root rotation
- root `srfAttachNode` local/world position when present
- first attached child part under the radial decoupler root, including child PID/name, local position, parent index, and child `srfAttachNode` world position
- values that can be reconstructed from the ghost snapshot or saved sidecars versus values that only exist live at break time
- whether the seed came from `joint-child-part-origin` or `new-vessel-root-part`
- distance from root origin to each candidate contact point

#### `BackgroundRecorder.InitializeLoadedState`

Log the seed conversion once per child debris recording:

- seed UT and current UT at initialization
- parent recording id and parent live vessel id/name
- parent anchor pose source and timestamp assumption
- absolute seed world position
- computed Relative local offset
- delta from first ordinary sample when both are available
- the parent-unavailable fallback path (`parentRec == null` or `parentVessel == null`), because it skips the Relative-open seed transform and changes the seed semantics entirely

#### `BackgroundRecorder` loaded-background sampling

Log the first ordinary physics sample for debris recordings:

- point UT and whether it is the first post-seed ordinary sample
- vessel lat/lon/alt-derived world position and rotation
- root part world position and rotation
- `absolutePoint` stored before Relative conversion
- Relative local offset after conversion
- root/contact candidate deltas when the root part is a radial decoupler

#### `GhostPlaybackEngine.TrackGhostAppearance`

Fix diagnostics so `recordingStart` reports the first active section frame when the active section is Relative. If it reports top-level flat `Recording.Points[0]`, label it as flat/fallback and do not print it as a Relative offset.

### Phase 2: Decide the target anchor

Use the Phase 1 logs to compare these candidates at the same split:

1. new vessel root part origin
2. radial decoupler `srfAttachNode` contact to the parent/core vessel
3. booster child `srfAttachNode` contact to the decoupler
4. `PartJoint` anchor/contact if exposed and stable
5. vessel COM, only as a diagnostic baseline

Expected result: the observed visual offset should match either a first-ordinary-sample time gap or one of the attach/contact deltas, not COM.

If the decoupler-side `srfAttachNode` and booster-child `srfAttachNode` disagree, prefer the candidate that matches the observed visual contact in the retained/run-time evidence. If neither node matches, do not force an attach-node correction; reserve the joint anchor/contact as the next candidate and keep the fix in diagnostics until the target point is proven.

### Phase 3: Implement contact-to-root pose conversion

If the attach/contact hypothesis is confirmed, add a helper that writes a root-part trajectory point from an explicit root world pose rather than always sampling `part.transform.position`.

Proposed helper shape:

```csharp
internal static bool TryCreateAbsoluteTrajectoryPointFromRootPose(
    Part rootPart,
    Vector3d rootWorldPosition,
    Quaternion rootWorldRotation,
    Vector3 velocity,
    double ut,
    out TrajectoryPoint point)
```

For radial/surface decoupler debris, keep the two sides of the calculation distinct:

```text
correctedRootWorld = targetContactWorld - rootWorldRotation * childRootLocalContactOffset
```

Where `targetContactWorld` is the parent-side or pre-break joint/contact/attach position captured at split time, and `childRootLocalContactOffset` is the equivalent contact point in the new debris root's local space. Do not derive both values from the same post-split child transform; that would collapse the formula back to the current root position and make the fix a no-op.

This correction is intentionally position-only. Preserve the root part rotation contract from `TryCreateAbsoluteTrajectoryPointFromPartOrigin`: `srfRelRotation = Inverse(body.bodyTransform.rotation) * part.transform.rotation`. Changing rotation to point at the contact would violate the ghost snapshot-root transform and likely rotate the booster subtree incorrectly.

Also verify that rotation survives the later Relative conversion path. Background relative sampling derives stored rotation from the vessel/root transform path while converting position into anchor-local metres; any root-pose helper must preserve the same root-part surface-relative rotation contract through both `TrackSection.frames` and `TrackSection.absoluteFrames`.

Candidate offset sources:

- root radial decoupler `srfAttachNode`: `root.transform.TransformPoint(root.srfAttachNode.position)`
- booster child `srfAttachNode`: `child.transform.TransformPoint(child.srfAttachNode.position)`, converted to root local
- stable `PartJoint` anchor/contact data if diagnostics prove it is reliable

KSPCommunityFixes uses the same surface-attach world-point pattern:

```csharp
part.transform.rotation * part.srfAttachNode.position + part.transform.position
```

### Phase 4: Apply the correction consistently

The correction must apply to every stored representation for the same debris recording. Fixing only the seed would create another bridge mismatch; fixing only the Relative frame would leave fallback paths and diagnostics with the old absolute pose.

If Phase 2 chooses a cached local offset, the per-sample equation should recompute from the current sample pose, not replay the split-time world contact:

```text
correctedRootWorld(t) = sampledRootWorld(t) + sampledRootRotation(t) * cachedRootLocalCorrection
```

Where `cachedRootLocalCorrection` is the root-local delta from the uncorrected root origin to the corrected root origin. Do not reuse `targetContactWorld` from the split event for later samples; that would freeze the contact in world space.

Smallest likely code surfaces:

1. `FlightRecorder.OnPartJointBreak`
   - capture the selected contact information with the pending structural seed, or capture a corrected root pose seed.
2. `ParsekFlight.OnDecoupleNewVesselDuringSplitCheck`
   - match the pending correction to the new vessel root PID.
   - store the correction on the child recording initialization path.
3. `BackgroundRecorder` split and sampling paths
   - apply the same separation-root correction metadata to foreground-created debris that later moves to loaded-background sampling.
   - cover background split children too, including child initial samples and structural-event samples, or explicitly scope them as a follow-up risk if diagnostics show they are not in the failing scenario.
   - account for `vessel.packed` fail-closed behavior in part-origin helpers. If a future background/warp path cannot sample the live root part because the vessel is packed, it must not silently fall back to an uncorrected post-split vessel pose.
4. `BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel` or a narrower debris-only wrapper
   - apply the stored correction before creating the absolute point used by `Recording.Points`.
   - keep `Recording.Points`, `TrackSection.frames`, and `TrackSection.absoluteFrames` consistent for both `sectionAuthoritative=True` and `sectionAuthoritative=False` recordings.
5. Recording metadata
   - if the correction cannot be rediscovered from live parts on every sample, store a per-recording local correction vector and source enum. Prefer a narrow field such as `SeparationRootLocalOffset` with a `SeparationRootOffsetSource`.
   - if Phase 1 proves the correction is reconstructable from existing live/snapshot data, do not add schema. Recompute once at split/background initialization and cache it in runtime state.
   - if persisted, treat this as a recording-schema change. Update `Recording`, `RecordingStore` format-version constants near the existing debris-parent format stamp, the matching binary-version/read-side legacy default, `RecordingTreeRecordCodec.SaveRecordingInto` and load logic using the existing sparse-write pattern, `ParsekScenario` save/load if the field appears in `.sfs`, test generators such as `RecordingBuilder` and `ScenarioWriter`, round-trip tests, legacy defaults, and `InvariantCulture` serialization.
   - use v12 `DebrisParentRecordingFormatVersion` / `DebrisParentRecordingBinaryVersion` as the template: sparse top-level field, stamp bump, read-side default for legacy recordings, and no binary layout churn unless the new field truly needs it.

Avoid changing generic `TrajectoryMath` Relative semantics. Format-v6 Relative points already use anchor-local metre offsets in `latitude`, `longitude`, and `altitude`; this bug is upstream of that contract.

### Phase 4a: Extract independent diagnostic cleanup

The `GhostAppearance` `recordingStart` mislabel for `sectionAuthoritative=False` is independent of the radial-origin fix. It should land first or be tracked as a standalone todo so future retained logs cannot confuse flat absolute LLA with Relative local metre offsets. This change is diagnostics-only and should not alter playback behavior.

### Phase 5: Old-recording compatibility decision

Exact correction of old recordings probably requires re-recording because current sidecars do not preserve the chosen contact-node world/local offset. A render-side heuristic could be added later, but it should be gated and explicit.

Possible heuristic:

- apply only to recordings whose ghost craft root part is a known radial decoupler and whose first child is a plausible booster subtree root;
- derive an approximate root-local visual correction from the first child local offset or reconstructed attach node if available;
- guard by recording format/version or a compatibility flag;
- log the correction source and magnitude.

Recommendation: do not implement this heuristic in the first fix. First make new recordings correct and prove the target reference point.

## Test Plan

### Unit tests

1. Contact-to-root math tests
   - pure internal static helper test with known root position, root rotation, local contact offset, and desired contact world point;
   - assert `rootWorld + rootRot * rootLocalContactOffset == desiredContactWorld` within tolerance;
   - include rotated roots so the test proves the local-to-world math, not only translation.

2. Stored-frame consistency tests
   - assert the corrected absolute pose is present consistently in `Recording.Points`, `TrackSection.frames`, and `TrackSection.absoluteFrames`;
   - cover both `sectionAuthoritative=True` and `sectionAuthoritative=False` recordings;
   - assert fallback / absolute-shadow playback cannot use an uncorrected pose when the Relative frame is corrected.

3. Background sample consistency tests
   - synthetic debris recording with stored `SeparationRootLocalOffset`;
   - seed and first ordinary sample use the same corrected root reference;
   - no artificial seed-to-next-frame bridge appears when parent anchor pose is stable.

4. Background split path tests
   - cover foreground split children that transition into loaded-background sampling;
   - cover background-created split children if the correction is intended to apply there;
   - otherwise add an explicit failing/ignored coverage note that background radial debris is out of scope for the first implementation.

5. Metadata / serialization tests
   - if `SeparationRootLocalOffset` is persisted, round-trip the field through the chosen sidecar/save path;
   - assert legacy recordings default to no correction;
   - assert the recording format/binary stamp and read-side default are covered;
   - assert serialization uses invariant culture.

6. Appearance diagnostic tests
   - for `sectionAuthoritative=False` with top-level flat points and Relative TrackSections, assert `GhostAppearance` debug text does not label flat LLA as a Relative local offset.

7. Undocking guard tests
   - undocking path continues to use the old root-part-origin behavior unless explicitly given a separate correction source;
   - radial decoupler correction does not apply to docking port debris/undock recordings.

### In-game tests

Real `Part`, `PartJoint`, and `srfAttachNode` behavior cannot be proven with pure xUnit fakes. Any test that depends on live KSP part topology belongs in `Source/Parsek/InGameTests/RuntimeTests.cs` with `[InGameTest(Scene = GameScenes.FLIGHT)]`.

1. Structural seed diagnostics
   - stage a radial side booster split;
   - assert logs include `joint.Child`, `joint.Parent`, `joint.Host`, `joint.Child.parent`, the `attachJoint == joint` result, seed source, root/contact deltas, and non-structural skip diagnostics when applicable.

2. Contact candidate comparison
   - record decoupler-side `srfAttachNode`, booster-child `srfAttachNode`, joint anchor/contact if available, root part origin, vessel pose, and COM in one run;
   - export enough log evidence to decide the target anchor and whether the correction is reconstructable after split.

### Runtime diagnostics / in-game validation

Run a focused radial booster recording after diagnostics land:

1. radial side boosters decouple from a core stack;
2. record KSP.log evidence for joint break, split callback, background initialization, and first ghost appearance;
3. verify the log shows the selected contact candidate and the root/contact delta;
4. run in-game playback and visually confirm whether the selected contact aligns with the decoupler/booster interface;
5. collect logs with `python scripts/collect-logs.py radial-debris-origin-after-diagnostics`.

If a code fix follows, repeat the same scenario and compare:

- first visible frame stays `activeFrame=Relative` and `firstFrameClamped=T` only if needed;
- no `SinglePoint` fallback appears;
- root/contact diagnostic delta is applied consistently to seed and ordinary samples;
- side booster visual center no longer appears several meters forward from the decoupler/contact.

### Headless validation

After any implementation:

```bash
dotnet build Source/Parsek/Parsek.csproj
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
```

If local xUnit execution is blocked by the known environment issue, use:

```bash
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
```

and record the blocker clearly.

## Risks

- Applying a render-side correction globally could move old recordings that were already visually acceptable.
- Correcting only the seed would reintroduce a bridge discontinuity once ordinary samples begin.
- COM alignment is tempting but wrong for rotated assemblies and would violate the snapshot-root transform contract.
- A persisted per-recording correction is a format-version change. Missing any of the format constant, binary stamp, codec, save/load, generator, or legacy-default surfaces can corrupt old or new recordings.
- `srfAttachNode` on the radial decoupler and on the booster child may point to different sides of the interface; diagnostics must decide which one matches the visual expectation.
- If neither `srfAttachNode` candidate matches the perceived alignment, the actual target may be the joint anchor/contact. Do not paper over that with a guessed node.
- `PartJoint` anchor data may not be stable or may be unavailable after the break event; log it first, then decide whether to trust it.
- Recomputing contact correction every background sample multiplies across active debris and conflicts with the visual efficiency principle. Prefer computing once at split/background initialization and caching or persisting the result.
- The existing `GhostAppearance` flat/section diagnostic mix can mislead verification unless fixed or read carefully.

## Branch Hygiene

This plan was committed on the investigation/diagnostics branch, separate from the main `Parsek/` checkout. Continue follow-up implementation in this worktree or a fresh sibling worktree, and keep the main checkout clean unless a task explicitly approves editing it.

## Non-Goals

- Do not redesign Relative frame semantics.
- Do not fix undocking in this branch unless new evidence shows the same radial-contact failure mode.
- Do not implement broad legacy recording migration before new-recording semantics are proven.
- Do not change ghost craft root selection unless diagnostics prove snapshot root choice itself is wrong.
- Do not close, restart, or otherwise manage KSP from the agent session for validation.
