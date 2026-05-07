# Recording & Ghost Rendering — Refactor Plan

Date: 2026-05-07
Last amended: 2026-05-07 (post-Opus review)
Branch: `claude/investigate-recording-policies-6tMZ9`
Companion document: `recording-and-ghost-policies-audit-2026-05-07.md`

## Goal

Fix the observed debris-rendering bugs **without introducing regressions in the working Re-Fly path.** Re-Fly recording and rendering currently work; nothing in this plan should change Re-Fly's data, code paths, or playback dispatch in ways that affect non-debris recordings.

## Why "isolation by adding code paths" is the wrong instinct

The two recorders are **already physically separate**: `FlightRecorder` (focused) and `BackgroundRecorder` (everything else), mutually exclusive by `tree.BackgroundMap` membership. They share two narrow seams that are correctly factored:

- `AnchorDetector.ShouldUseRelativeFrame` (`AnchorDetector.cs:311`) — frame hysteresis decision.
- `AnchorDetector.IsRecordingAnchorEligible` (`AnchorDetector.cs:190`) — recording-level eligibility, called by both recorders.
- `FlightRecorder.IsStructuralJointBreak` (`FlightRecorder.cs:1109`) — joint-break classification, called by both joint-break handlers.

The Absolute/Relative contract is decided per-section by `section.referenceFrame` — there is no scenario-aware branching anywhere in playback. **However**, the audit oversimplified by calling this a "single playback dispatch": the downstream world-position math is dispersed across `IGhostPositioner` methods (`InterpolateAndPosition`, `InterpolateAndPositionRelative`, `PositionLoop`, etc.) implemented in `ParsekFlight.cs` (~15961-16082+), with additional per-frame `referenceFrame` checks in `GhostPlaybackEngine.cs` at lines 4578, 4631, 4647. The dispatcher is *not* scenario-aware, but it is *physically dispersed*. That matters for harness scope (see Step 1 amendment) and for understanding the resolver's blast radius.

**The actual problems the bugs come from are two different things, both addressable without splitting any working code path:**

1. **Debris policy is implicit, not contractual.** It's "nearest-eligible-anchor, like every other vessel." That's an emergent property of code that wasn't written with debris in mind — there's no contract saying "debris should be anchored to its parent." Mental models around the codebase assume there is one.
2. **No regression harness on the resolver/positioning chain.** Re-Fly works today. Nothing prevents tomorrow's fix from silently breaking it.

So the strategy is: **add tripwires (regression harness, scoped to what we can test) and contract clarity (explicit debris policy). Implement the fix at the recorder. Touch the resolver only with a tightly-guarded fallback that cannot fire on non-debris recordings.**

---

## Decisions (confirmed by user 2026-05-07)

1. **Harness location:** `Source/Parsek.Tests` (xUnit). Deterministic mocks where they can be added cheaply.
2. **Debris fallback when parent unresolvable / > 2500m:** parent's last known *Absolute* pose + debris's recorded offset. (See Step 2 §"Defining 'parent's last known pose'" for the precise semantics — there is a CLAUDE.md gotcha here.)
3. **Cascade cap:** stays at `MaxRecordingGeneration = 1`. Secondary debris remains untracked.
4. **Step 4 cleanups:** defer to a separate PR after the debris fix lands.
5. **Recording-time distance source for `ShouldUseRelativeFrame`:** Option C — when `recording.AnchorRecordingId != null`, always record Relative, skip the 2300/2500m hysteresis at recording time. Playback fallback handles parent-far / parent-gone cases. Simplest, fewest moving parts.
6. **Primary bug surface:** focused-vessel debris (booster decouples during ascent / staging). Background-vessel debris is secondary. This means `ParsekFlight.CreateBreakupChildRecording` is the **highest-priority** of the three creation sites — Step 3b prioritizes it.
7. **Loop-debris and control-transfer:** no special cases. Contract is simply `child.AnchorRecordingId = parent.RecordingId` for any debris child. If a parent recording happens to have `LoopAnchorVesselId != 0`, the resolver's existing loop-anchor rejection (`RelativeAnchorResolver.cs:154`) plus the new debris fallback combine to produce Absolute playback for free — no special-case code, no dependency on which subsystem set the loop field. Debris vessels by definition have no controller and can't be focused-and-flown.

---

## Step 1 — Regression harness (do this first, no behavior changes)

### Scope reduction (post-Opus-review)

The original plan claimed the harness would hash "world positions over UT span" — i.e. drive the full ghost-position pipeline. That is **not feasible without a refactor first.** The actual world-position math lives in `IGhostPositioner` methods on `ParsekFlight`, which is a `MonoBehaviour` with hundreds of fields tied to scene state (`flightCamera`, `FlightGlobals.ActiveVessel`, `Time.deltaTime`). Driving them from xUnit requires either a `MonoBehaviour` instance (impractical) or extracting the math into static helpers (refactor that the plan defers).

**Reduced scope: hash the outputs of `RelativeAnchorResolver.TryResolveRecordingPose` (and friends) over a UT span.** This covers:

- Frame dispatch decision per section (`section.referenceFrame`).
- Anchor-resolution chains (recursive `TryResolveAnchorPose`).
- Re-Fly anchor walk-back (`TryResolveActiveReFlyAnchorRecording`).
- Loop-anchor live-PID rejection.
- Same-chain continuation lookup.

What it does **NOT** cover (be explicit so we don't pretend otherwise):

- Positioner methods (`InterpolateAndPosition*` in `ParsekFlight.cs`).
- Terrain clamping, ground-clearance correction, surface skip.
- Camera follow, ghost mesh building, map orbit rendering, tracking-station markers.
- Per-frame engine state (engine throttle, decoupler firings — these have their own test surfaces).

This means the harness catches "the resolver returns the wrong (pos, rot) for this UT" but it does NOT catch "the positioner snaps the ghost into the ground." The latter is a known gap; if a positioner-level regression bites, we extract the math to static helpers as a follow-up and extend the harness then.

### Why xUnit

- Tests run in CI on every PR.
- The existing test generators (`Source/Parsek.Tests/Generators/RecordingBuilder.cs`, `VesselSnapshotBuilder.cs`, `RecordingStorageFixtures.cs`) already build synthetic recordings; we extend.
- `RelativeAnchorResolver` is mostly static methods with a context parameter — testable.
- Required mocks: a small `IPlaybackBody` interface (or test-fake `CelestialBody`) so the resolver's Absolute path doesn't call into Unity. **This is a small piece of work but not zero;** it's the prerequisite for the harness and lives in PR 1.

### Scenarios to cover (initial set — extend as needed)

| # | Scenario | Why |
|---|----------|-----|
| 1 | Single recording, Absolute throughout | Baseline — simplest possible resolver call |
| 2 | Single recording, Relative throughout, fixed anchor | Verifies basic Relative resolver path |
| 3 | Single recording, Absolute↔Relative transitions mid-recording | Verifies hysteresis-driven section boundaries |
| 4 | Focused-vessel debris (parent in tree, debris has parent-anchor contract) | The currently-broken case |
| 5 | Background-vessel debris (parent in tree, debris has parent-anchor contract) | The other broken case |
| 6 | Re-Fly: provisional supersedes origin, both replayed across the merge | Verifies Re-Fly walk-back doesn't drift |
| 7 | Re-Fly + debris created during re-fly | Cross-cutting case, highest regression risk |
| 8 | On-rails background vessel (orbit segments only, no track sections) | Verifies on-rails path still passes |
| 9 | Loop-anchor recording (live-PID fallback) | Verifies loop rejection still produces "out of scope" warn, not a bad anchor |
| 10 | Same-chain continuation (anchor's recorded UT range doesn't cover requested UT, falls through to chain lookup) | Verifies chain-aware Re-Fly preserves order |

### What we hash

For each scenario, sample the resolver's `(worldPos, worldRot)` output at `N` evenly-spaced UTs (proposed `N = 32`) and concatenate as `(x, y, z, qx, qy, qz, qw)` doubles formatted with `R` invariant-culture, hash with SHA-256. Quaternion is included because rotation is part of the resolved pose contract; debris bugs sometimes manifest as rotation drift.

### Hand-off criterion

Before any code in Step 2/3 lands, the harness:
- Has scenarios 1, 2, 6, 9 passing on `main`'s current behavior (these don't change in Step 3).
- Scenarios 4, 5, 7 are captured **as a baseline**, not as "correct" — they encode whatever the current incorrect behavior is. Step 3 explicitly resets these hashes with a justification.
- CI runs it on every PR.

### Estimated effort

Realistic: 1-2 weeks for the initial harness including the `IPlaybackBody` mock infrastructure. The "few days" estimate in the original plan was optimistic — the Unity-mock work is non-trivial.

---

## Step 2 — Debris contract definition (one page, before any code)

### Background on the rendering link

Today's playback already renders a vessel ghost together with its debris children — they appear visually linked. What's wrong is the *spatial data*: the debris's anchor at sample time was "nearest eligible vessel," which is usually but not always the parent. So the visually-linked rendering shows debris in the wrong place when the recorded anchor wasn't the parent. The contract below makes the recorded data match what the visual link already implies.

### Proposed contract

> **Debris is anchored to its parent recording for its entire lifetime.**
>
> When a split produces a child recording marked `IsDebris = true`:
>
> 1. The child's `Recording.AnchorRecordingId` (a new top-level field) is set to the parent's `RecordingId` at registration time. No special cases.
> 2. When `AnchorRecordingId != null`, every Relative `TrackSection` written into the child uses that anchor — the per-frame nearest-anchor search is **skipped** for those recordings.
> 3. **Recording-time:** Option C (per Decision §5). Always record Relative. The 2300/2500m hysteresis is bypassed at recording time — the playback fallback handles parent-far / parent-gone cases.
> 4. At playback, if the anchor recording resolves but the resulting parent pose is unresolvable at the requested UT (parent destroyed, parent superseded and Re-Fly walk-back returns nothing, parent rejected as loop anchor) **OR** is more than `2500 m` from the debris's recorded relative offset, the section falls back to **Absolute** by reading the parent's last-known Absolute pose + the debris's offset (see definition below).
> 5. The fallback in (4) is **only** reachable when `recording.IsDebris == true && recording.AnchorRecordingId != null && resolver returned no pose`. Non-debris recordings can NEVER enter this fallback.
> 6. The cascade cap (`MaxRecordingGeneration = 1`) stays — secondary debris (debris of debris) is still not recorded.

### Edge cases this contract addresses

| Edge case | Current behavior (bug) | Proposed behavior |
|-----------|------------------------|-------------------|
| Booster decouples, drifts away | Anchor flips to whatever is nearest, then to Absolute past 2500m → ghost can teleport when the late-life "anchor" is some other vessel that moved | Anchor stays parent for life; smooth fallback to Absolute once parent is gone or far |
| Parent re-flied (supersede chain) | Re-Fly resolver chases anchor through chain, but also through any other anchor the debris picked up during sample time → unpredictable | Anchor is always parent; resolver only chases parent supersedes |
| Debris of debris | Silently dropped (cascade cap) | Still silently dropped — same behavior, but now explicitly contracted |
| Parent destroyed mid-debris-life | Debris ghost may continue rendering with stale anchor pose | Defined fallback: Absolute from parent's last known pose |
| **Parent recording has `LoopAnchorVesselId != 0`** | Resolver rejects loop recording as anchor → debris falls back via the new Absolute fallback path | Same: fallback fires uniformly; debris renders at parent's last known Absolute pose + offset. No special case in the contract — the resolver's existing loop-anchor rejection (`RelativeAnchorResolver.cs:154`) plus the new fallback compose correctly. The contract doesn't need to know how `LoopAnchorVesselId` came to be set on the parent. |
| **Cross-tree anchor** | Today's `IsRecordingAnchorDAGOrderEligible` allows cross-tree | Debris is always same-tree (parent is by construction). Contract is no-op for this case but harness scenario should verify |

### Defining "parent's last known pose" precisely

This is **the highest-blast-radius land mine** if specified imprecisely (per Opus review §I4). CLAUDE.md gotcha: in `ReferenceFrame.Relative` sections (v6+), the per-point `latitude/longitude/altitude` fields actually store anchor-local Cartesian dx/dy/dz **in metres**, not body-fixed lat/lon. Reading them naïvely and feeding to `body.GetWorldSurfacePosition` produces a position deep inside the planet.

**Definition (binding):**

The "parent's last known Absolute pose at UT t" is the world position + rotation derived as follows:

1. Find the latest `TrajectoryPoint` in the parent recording with `ut <= t` whose owning section has `referenceFrame == Absolute`. That point's `latitude/longitude/altitude` are body-fixed and may be passed to `body.GetWorldSurfacePosition`. Rotation: `body.bodyTransform.rotation * srfRelRotation`.
2. If no Absolute point exists at or before `t` (e.g. parent recording is purely Relative), the fallback **fails** — return false from the resolver and let the caller decide whether to skip the frame or render at last-known. **Do NOT reinterpret a Relative point as Absolute.**
3. The fallback caches the resolved Absolute pose by `(parentRecordingId, t)` to avoid repeated walks for adjacent debris frames within the same physics tick.

### `IsDebris` propagation surface (post-review v2)

The plan must propagate `Recording.AnchorRecordingId` through **every site that propagates `IsDebris`**. The first review caught the three primary creation sites; the second review caught five secondary propagation sites the plan had ignored. Complete enumeration:

**Primary creation sites (set `IsDebris = true` on a brand-new Recording):**

1. `BackgroundRecorder.RegisterChildRecordingsFromSplit` at `BackgroundRecorder.cs:1115` (BG-vessel split, registers in tree).
2. `BackgroundRecorder.BuildBackgroundSplitBranchData` at `BackgroundRecorder.cs:1176` (pure-static factory, called from path 1's parent).
3. `ParsekFlight.CreateBreakupChildRecording` at `ParsekFlight.cs:5103` (focused-vessel debris via crash coalescer, called from `ParsekFlight.cs:5497, 5552`). **Highest-priority — the dominant observed-bug surface.**

**Secondary propagation sites (copy `IsDebris` from one Recording to another):**

4. `Recording.cs:569` — `ApplyPersistenceArtifactsFrom` / `DeepClone`. Used in tree edits and saves. Without propagation, every cloned debris recording silently loses the contract.
5. `SessionMerger.cs:135` — `merged.IsDebris = srcRec.IsDebris` during session merges.
6. `RewindInvoker.cs:954` — `provisional.IsDebris = inheritFrom.IsDebris` during Re-Fly inheritance. **Critical for Re-Fly safety:** without this, a re-fly of a flight with debris children silently loses the contract for the provisional.
7. `RecordingOptimizer.cs:931` — `second.IsDebris = original.IsDebris` in `SplitAtSection` (second half of an optimizer split).
8. `BackgroundRecorder.cs:673` — parent-continuation recording in BG-split branch: `IsDebris = parentRec.IsDebris`.

**Read sites (no propagation needed but useful for tests):**
- `RecordingTreeRecordCodec.cs:744` — load. Must default `AnchorRecordingId = null` on legacy versions; explicit symmetric write at `RecordingTreeRecordCodec.cs:215` for new field.

**Implementation strategy:** introduce a single helper `ApplyDebrisAnchorContract` (Step 3b) and call it adjacent to every primary `IsDebris = true` assignment. For secondary copy sites, propagate the new field on the same line: `dest.AnchorRecordingId = src.AnchorRecordingId;` immediately after each `dest.IsDebris = src.IsDebris;`.

**Unit test obligation (Step 1 / harness):** every numbered site above gets a unit test that exercises the propagation. If the harness scenarios capture broken behavior at any site, the propagation isn't complete.

### What *changes* at recording time

- New field: `Recording.AnchorRecordingId` (string, default null), persisted in ConfigNode.
- All three creation sites set it for `IsDebris` children. No exceptions.
- BackgroundRecorder per-frame sampling: when `recording.AnchorRecordingId != null`, write that as `section.anchorRecordingId` and skip the nearest-anchor search.
- `AnchorDetector.ShouldUseRelativeFrame` distance source: when `recording.AnchorRecordingId != null`, distance is to parent recording's pose at sample time. Requires the recorder to invoke the resolver — **acknowledged inversion of dependency** that adds non-trivial implementation risk (see Step 3b notes).

### What *doesn't* change

- The `referenceFrame`-based dispatch in `GhostPlaybackEngine` and the positioner methods. Untouched.
- The `RelativeAnchorResolver` chain for non-debris recordings. Untouched.
- Re-Fly walk-back for non-debris. Untouched.
- Non-debris background vessels. Untouched.
- The cascade cap.

### Format version

The new top-level `Recording.AnchorRecordingId` is written by `RecordingTreeRecordCodec.SaveRecordingInto` (ConfigNode codec). The binary `.prec` codec is **not affected** — it stores trajectory data (points, sections, orbit segments, events), not top-level Recording fields.

- ConfigNode codec: bump `RecordingFormatVersion` to 12, add `DebrisAnchorChainFormatVersion = 12 = CurrentRecordingFormatVersion` named constant in `RecordingStore.cs:57-65`.
- Binary codec: **no version bump** (the per-section `anchorRecordingId` field already exists since v11).
- Load-time: legacy recordings get `AnchorRecordingId = null` and use legacy nearest-anchor search.

---

## Step 3 — Implement the debris contract

**Slicing (post-review):** Step 3 splits into three PRs (3a, 3b, 3c) for safer revert. Original plan had this as one atomic PR; the review flagged that as overcautious in the wrong direction.

### 3a. Schema (PR 3a)

- Add `Recording.AnchorRecordingId` (string, default null).
- ConfigNode codec **write** in `RecordingTreeRecordCodec.SaveRecordingInto` (alongside the existing `IsDebris` write at line 215 area).
- ConfigNode codec **read** in the load path adjacent to `RecordingTreeRecordCodec.cs:744` (`isDebris` load).
- Format-version constant + bump per "Format version" section above.
- Load-time default-to-null on legacy versions (recordings with `RecordingFormatVersion < 12` get `AnchorRecordingId = null` and continue using legacy nearest-anchor at recording time + legacy shadow at playback).
- Save/load round-trip tests.
- **No recorder change. Field is unused. Bisect-safe.**

### 3b. Recorder (PR 3b) — primary fix, load-bearing

**This is the load-bearing PR for the dominant bug.** PR 3c (resolver fallback) is belt-and-suspenders for the parent-gone tail, not the primary defense. The dominant observed bug ("debris ghost teleports / desyncs") is fixed by writing the *correct* `anchorRecordingId` into the section at recording time. The resolver-side fallback only catches the smaller "parent gone" case.

#### Helper

Introduce two overloads (one Recording-typed, one string-typed for the static factory):

```
internal static void ApplyDebrisAnchorContract(Recording child, Recording parent)
{
    if (!child.IsDebris) return;
    child.AnchorRecordingId = parent?.RecordingId;
}

internal static void ApplyDebrisAnchorContract(Recording child, string parentRecordingId)
{
    if (!child.IsDebris) return;
    child.AnchorRecordingId = parentRecordingId;
}
```

#### Primary creation sites — invocation and ordering

1. `ParsekFlight.CreateBreakupChildRecording` (`ParsekFlight.cs:5103`) — call helper immediately after the new Recording is constructed, before it's returned to the caller.
2. `BackgroundRecorder.RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`) — **ordering matters** (per review §S3). Today the code at lines 1097-1115 registers the recording in the tree and initializes sampling state *before* setting `IsDebris = true` at line 1115. The helper must be moved earlier so `AnchorRecordingId` is set **before** `tree.AddOrReplaceRecording(child)` (line 1097) and before `OnVesselBackgrounded(...)` (line 1108). Concretely: refactor lines 1097-1129 so the `IsDebris` and `AnchorRecordingId` assignments happen at the top of the per-child loop iteration, then registration, then sampling init.
3. `BackgroundRecorder.BuildBackgroundSplitBranchData` (`BackgroundRecorder.cs:1143`) — call helper inside the loop at lines 1163-1180, immediately after the constructor sets `IsDebris`.

#### Secondary propagation sites

Per the IsDebris-propagation enumeration above, also propagate at:

- `Recording.cs:569` (`ApplyPersistenceArtifactsFrom`): add `AnchorRecordingId = source.AnchorRecordingId;` immediately after the `IsDebris` line.
- `SessionMerger.cs:135`: add `merged.AnchorRecordingId = srcRec.AnchorRecordingId;`.
- `RewindInvoker.cs:954`: add `provisional.AnchorRecordingId = inheritFrom.AnchorRecordingId;`. Re-Fly safety depends on this.
- `RecordingOptimizer.cs:931`: add `second.AnchorRecordingId = original.AnchorRecordingId;`.
- `BackgroundRecorder.cs:673` (parent-continuation): add `AnchorRecordingId = parentRec.AnchorRecordingId,` to the object initializer.

#### Optimizer changes (per review §S5)

- `RecordingOptimizer.CanAutoMerge` (`RecordingOptimizer.cs:26`): after the existing `LoopAnchorVesselId` mismatch guard at line 65, add an `AnchorRecordingId` mismatch guard. Two debris recordings with different parents cannot auto-merge — anchor mismatch is silent corruption of the parent-anchor contract.
- `SplitAtSection` propagation is covered above.

#### Per-frame anchor write

In `BackgroundRecorder` sampling loop: when `recording.AnchorRecordingId != null`, set `section.anchorRecordingId = recording.AnchorRecordingId` and skip the candidate-list / nearest-search.

#### Recording-time frame decision (Option C, per Decision §5)

When `recording.AnchorRecordingId != null`, the recorder always writes Relative sections — no distance computation, no hysteresis at recording time. The playback fallback handles parent-far / parent-gone uniformly. No resolver call from the recorder, no recursion concerns.

### 3c. Resolver fallback (PR 3c) — belt-and-suspenders, NOT the primary fix

**Reframing (per review §S2):** the dominant observed bug ("debris ghost teleports / desyncs because the recorded anchor was a third party that itself moved") is fixed by **PR 3b**, not by this PR. By the time playback runs, the section's `anchorRecordingId` has already been written by the recorder; if PR 3b wrote the parent's ID, the resolver succeeds and no fallback is needed. This PR catches only the smaller cases where the parent is *gone* at playback (destroyed, loop-rejected, missing from the save).

PR 3c could in principle be deferred — but it's small and lands the contract's fail-safe in a single PR.

The fallback case "parent unresolvable or > 2500m from offset" lives in `RelativeAnchorResolver.TryResolveRelativeSectionPose` (`RelativeAnchorResolver.cs:540-612`).

**Critical guard (per review §R3):** the function is called recursively from `TryResolveAnchorPose` (line 199). The fallback must **only** fire when:

```
recording.IsDebris == true
  && recording.AnchorRecordingId != null
  && recording.RecordingFormatVersion >= DebrisAnchorChainFormatVersion  // 12
  && (anchorPoseResolutionFailed || distance(parentPose, debrisOffset) > 2500m)
```

The format-version gate (per review §S4) is essential. The positioner already has its own absolute-shadow fallback path in `ParsekFlight.cs:21852` (`TryUseRelativeAbsoluteShadowFallback`) that consumes `TrackSection.absoluteFrames` (the v7 shadow). For legacy v7 recordings, that path must continue to work; the new resolver fallback must not pre-empt it. Gating on format ≥ 12 means: only debris recordings authored *after* this PR use the new fallback; legacy v7 debris keeps using its existing shadow path.

Non-debris recordings, or debris recordings without `AnchorRecordingId`, or legacy-format debris, all take the existing `WarnUnresolved` path. **Add explicit unit tests** that exercise:
- Non-debris Relative anchor chains — fallback must never fire.
- Legacy v7 debris with absolute-shadow — must continue using positioner-side shadow, not the new resolver fallback.
- v12 debris with parent-gone — fallback fires, returns Absolute pose.

Order of operations inside the resolver (per previous review §E7):
1. Anchor lookup.
2. Same-chain continuation lookup (line 247) — preserves chain-aware Re-Fly.
3. Re-Fly walk-back (`TryResolveActiveReFlyAnchorRecording`).
4. **Then** the new debris fallback (gated as above).
5. Then `WarnUnresolved` + return false.

The "parent's last known Absolute pose" lookup uses the precise definition in Step 2 §"Defining 'parent's last known pose'."

#### Loop-parent-with-no-Absolute-points: documented limitation

If a parent recording has `LoopAnchorVesselId != 0` *and* no Absolute sections (purely Relative throughout — possible for orbital station loops), the resolver rejects the parent as anchor (line 154), the new fallback walks back looking for an Absolute point, and finds none. Per the binding definition, the fallback then refuses to resolve and returns false. The debris ghost is then **invisible** for that frame.

This is a tradeoff vs v2 of the plan, which would have routed such debris through the legacy nearest-anchor path (producing a possibly-wrong-but-visible ghost). User has confirmed (Decision §7) that no special case is wanted; if this limitation produces visible bugs in practice, revisit by either authoring a parent-anchor-shadow at recording time (Option B from earlier discussion) or by relaxing the resolver's loop rejection for debris-of-loop. Not in this plan.

### 3d. Re-run harness

Scenarios 4, 5, 7 hashes will change after PR 3c. Reset them in PR 3c with a comment: `// reset: debris parent-anchor contract introduced, see refactor-plan.md`. Scenarios 1, 2, 3, 6, 8, 9, 10 must NOT change. If any of them does, stop and investigate before merging.

---

## Step 4 — Zero-logic-change cleanups (deferred to post-fix PR)

### 4a. Constant migration to ParsekConfig (pure cosmetic)

- `BackgroundRecorder.MaxRecordingGeneration = 1` → `ParsekConfig.Recording.MaxRecordingGeneration`
- `BackgroundRecorder.DebrisTTLSeconds = 60.0` → `ParsekConfig.Recording.DebrisTTLSeconds`

### 4b. Audit-correction documentation

The audit document said `AnchorEligibility` is duplicated across recorders. **It isn't** — `AnchorDetector.IsRecordingAnchorEligible` is shared. Update the audit's coupling table accordingly.

The audit also called the playback dispatch "single" — it isn't; it's dispersed across `IGhostPositioner` methods. Update the audit's "Coupling Analysis" row #1 to clarify.

### 4c. What's NOT a refactor target

- `BuildRecordingAnchorCandidateList` (focused) vs `BuildBackgroundRecordingAnchorCandidates` (background) — different jobs, share `IsRecordingAnchorEligible`. Don't unify further.
- `IsLiveRecordingAnchorVesselCandidate` (`FlightRecorder.cs:5048`) — focused-only by design. Don't move.
- The two `onPartJointBreak` handlers — correctly bifurcated.
- The crash-coalescer vs direct-deferred-check split for debris registration — keep as-is. Different timing semantics for legitimate reasons (#362, #263).

### 4d. NOT in this plan

- Re-Fly singleton constellation (`ActiveReFlySessionMarker` + `SpawnSuppressedByRewind` + journal phase). Tangled but works.
- Cascade cap.
- Positioner extraction from `ParsekFlight` — would unblock fuller harness but is out of this plan's scope. Re-evaluate if a positioner-level regression bites.

---

## Risk analysis (updated v5)

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| Harness fails to capture a positioner-level regression | **Medium-high (acknowledged gap)** | Scoped to resolver level. Document gaps. Extend harness only if a positioner-level bug actually bites; until then, accept as known limitation. |
| Step 3 misses an `IsDebris` propagation site (clones, merge, Re-Fly inheritance, optimizer split) | **High if not enumerated; bounded now** | Eight sites enumerated in §"`IsDebris` propagation surface". Each gets a unit test. Without this, cloned / merged / re-flied debris silently lose the contract. |
| **Re-Fly inheritance loses the contract** | High if `RewindInvoker.cs:954` propagation is omitted | Explicit propagation requirement at §"Secondary propagation sites". Re-Fly + debris harness scenario (#7) catches drift. |
| Helper invocation ordering wrong → first-frame sampling sees `AnchorRecordingId == null` | High if not specified | Step 3b explicit ordering: helper called **before** `tree.AddOrReplaceRecording` and `OnVesselBackgrounded`. |
| Step 3c fallback pre-empts legacy v7 absolute-shadow path | **High if not gated** | Format-version gate `RecordingFormatVersion >= 12` in the triple-conjunction guard. Legacy v7 debris keeps using `ParsekFlight.cs:21852` shadow path. Unit test for v7 debris confirms. |
| Step 3c fallback fires on non-debris recordings → breaks Re-Fly | **High if guard is wrong** | Quadruple-conjunction guard (`IsDebris && AnchorRecordingId != null && format >= 12 && resolution-failed`). Unit tests exercise non-debris chains and assert fallback never invoked. |
| "Parent's last known pose" reads a Relative point as Absolute | **High if not specified** | Binding definition in Step 2: walk back to last Absolute point only. Refuse to fallback if no Absolute point exists. Do NOT reinterpret Relative lat/lon as body-fixed. |
| Loop-parent-with-no-Absolute debris becomes invisible | Acknowledged tradeoff | Documented as a limitation in §3c. Tradeoff: invisible (current plan) vs wrong-but-visible (v2's nearest-anchor fallback). User has accepted invisible. Revisit only if observed. |
| `RecordingOptimizer.CanAutoMerge` merges debris with mismatched parents | Medium | Add `AnchorRecordingId` mismatch guard at `RecordingOptimizer.cs:65` area, alongside the existing `LoopAnchorVesselId` guard. |
| Re-Fly settle gap → fallback walks back to pre-rewind pose | Low (bounded; debris is short-lived) | Acknowledged. If observed, address by skipping fallback for sections inside settle-suppression window. |
| Format-version bump corrupts legacy saves | Low | ConfigNode codec only. Standard load-time default-on-legacy. |
| Recording-time parent-pose resolution introduces recursion / performance issues | Eliminated by Option C | Option C in Step 3b (always Relative when contract applies, skip hysteresis at recording time) sidesteps the problem entirely. |
| Harness mock infrastructure is harder than estimated | Medium | Initial PR 1 includes `IPlaybackBody` mock setup. If it bogs down, fall back to "test resolver math against pre-computed expected outputs" without the body abstraction. |
| Audit document still has stale claims that the plan reads from | **Eliminated in v5** | Audit `recording-and-ghost-policies-audit-2026-05-07.md` updated in same PR as plan v5: lines 29-43 (single dispatch correction), 157-161 (same), 217-218 (anchor-eligibility duplication / dispatch correction). |

---

## Proposed PR sequence (revised)

1. **PR 1 — Harness skeleton.** `IPlaybackBody` (or fake `CelestialBody`) infrastructure + scenarios 1, 2, 6, 9 (no debris, no behavior changes). Lowest risk. Establishes tripwire for non-debris paths before any contract change.
2. **PR 2 — Harness debris baselines.** Scenarios 4, 5, 7, 10 capture *current* (broken) debris behavior and same-chain continuation behavior. Lowest risk; just adds coverage.
3. **PR 3a — Schema.** Add `Recording.AnchorRecordingId` field, ConfigNode codec, format version 12. Field unused. Bisect-safe.
4. **PR 3b — Recorder + helper.** Add `ApplyDebrisAnchorContract` helper. Call from all three creation sites. Per-frame anchor write. Decision needed on Option A/B/C for distance-source.
5. **PR 3c — Resolver fallback + hash resets.** Guarded fallback in `TryResolveRelativeSectionPose`. Reset scenarios 4, 5, 7 hashes with justification. Scenarios 1, 2, 3, 6, 8, 9, 10 must remain unchanged. **Most reviewable point — biggest behavior change in smallest diff.**
6. **PR 4 (optional) — Step 4 cleanups.**

Each PR independently shippable; bisect-friendly. PR 3c can be reverted alone without losing PR 3a's schema or PR 3b's recorder data.

---

## Open questions

None remaining. All decisions confirmed by user 2026-05-07. Plan is implementation-ready pending one final sanity pass.

---

## Success criteria

- Observed debris bugs no longer reproduce on the user's known-broken cases (verified manually in-game).
- All Re-Fly tests (existing in-game + harness scenarios 6, 7) still pass.
- Scenario hashes for non-debris non-Re-Fly cases (1, 2, 3, 6, 8, 9, 10) are unchanged across PR 3c.
- The quadruple-conjunction guard in Step 3c has explicit unit tests proving the fallback can't fire on (a) non-debris recordings, (b) legacy v7 debris (preserves shadow path), (c) debris with `AnchorRecordingId == null`.
- All eight `IsDebris` propagation sites have unit tests proving `AnchorRecordingId` flows through.
- `RecordingOptimizer.CanAutoMerge` has explicit test rejecting two debris with mismatched `AnchorRecordingId`.
- Audit document (`recording-and-ghost-policies-audit-2026-05-07.md`) is updated in the same PR as plan v5: stale "single dispatch" and "duplicated anchor candidate building" claims fixed.
- No new tangled coupling introduced (Step 4d list is preserved).

---

## Review history

- **2026-05-07, v1 (commit `d3bb84a`):** Initial plan. Single-PR Step 3, single-recorder-site change, harness scoped to "world positions over UT span."
- **2026-05-07, v2 (commit `e860601`):** Post-Opus-review amendments:
  - Acknowledged audit's "single playback dispatch" was an oversimplification — dispatch is dispersed across `IGhostPositioner` methods.
  - Added the missed third creation site (`ParsekFlight.CreateBreakupChildRecording`, line 5103) — the dominant focused-vessel-debris case.
  - Added the loop-debris exception (parent has `LoopAnchorVesselId != 0` → no contract).
  - Defined "parent's last known Absolute pose" precisely to avoid the Relative-lat/lon CLAUDE.md gotcha.
  - Tightened Step 3c guard to triple-conjunction; added explicit non-debris unit-test requirement.
  - Reduced harness scope to resolver-level outputs; documented positioner-level gap.
  - Split PR 3 into 3a (schema) / 3b (recorder) / 3c (resolver) for safer revert.
  - Clarified that the binary codec does NOT need a version bump (top-level field is ConfigNode-only).
  - Added scenarios 9 (loop) and 10 (same-chain continuation) to the harness.
- **2026-05-07, v3 (commit `5fe7048`):** Post-discussion-with-user simplifications. User clarified: (a) debris vessels by definition have no controller and can't be focused-and-flown; (b) playback already renders parent ghost together with debris children visually linked — what's broken is the spatial data, not the linkage.
  - Removed loop-parent exception from contract item 1.
  - Removed "debris becomes focused vessel" edge case.
  - Confirmed Option C for recording-time frame decision (Decision §5).
  - Marked focused-vessel debris as the highest-priority creation site in Step 3b (Decision §6).
  - Added the rendering-link background to Step 2.
  - Cleared the Open Questions section.
- **2026-05-07, v4 (commit `3f83be4`):** Stripped Gloops-specific reasoning. User flagged that Gloops module separation and dev is incomplete; the plan should not depend on assumptions about which code paths produce loop recordings. The loop-parent simplification stands but is now justified solely by the resolver-rejection + new-fallback composition — Gloops-independent. No behavior or schema changes vs v3.
- **2026-05-07, v5 (this revision):** Second-round Opus review found multiple gaps in v4. Document-only fixes:
  - **Reframed PR 3b as load-bearing for the dominant bug**, PR 3c as belt-and-suspenders for the parent-gone tail. Earlier wording overstated the resolver fallback's role.
  - **Enumerated all eight `IsDebris` propagation sites** (three primary creation + five secondary copy). Plan v4 had only the three primary. Critical gap: `RewindInvoker.cs:954` Re-Fly inheritance and `Recording.cs:569` clones would have silently lost the contract.
  - **Specified helper invocation ordering** in `RegisterChildRecordingsFromSplit` — must run before `tree.AddOrReplaceRecording` and `OnVesselBackgrounded`, not after the existing `IsDebris = true` line at 1115.
  - **Added format-version gate** to the resolver fallback: `RecordingFormatVersion >= DebrisAnchorChainFormatVersion` (i.e. ≥12). Legacy v7 debris keeps using the existing `ParsekFlight.cs:21852` absolute-shadow fallback. The new fallback applies only to v12+ recordings. Guard is now quadruple-conjunction.
  - **Added optimizer interactions**: `RecordingOptimizer.CanAutoMerge` mismatch guard for `AnchorRecordingId`; `SplitAtSection` propagation already covered by the secondary-sites enumeration.
  - **Added helper string-overload** for the pure-static `BuildBackgroundSplitBranchData` factory (which takes `parentRecordingId : string`, not a `Recording`).
  - **Schema PR 3a** now explicitly covers ConfigNode read symmetry in addition to write.
  - **Documented loop-parent-with-no-Absolute-points limitation** explicitly: such debris is invisible at playback. Tradeoff vs v2's wrong-but-visible. User accepted (Decision §7).
  - **Added Re-Fly-settle-gap edge case** acknowledgement (low severity; debris is short-lived).
  - **Audit document updated in tandem**: stale "single dispatch" and "duplicated anchor candidate building" claims fixed at audit lines 29-43, 157-161, 217-218.
