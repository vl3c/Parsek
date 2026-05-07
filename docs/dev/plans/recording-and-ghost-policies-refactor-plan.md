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

So the strategy is: **add tripwires (regression harness, scoped to what we can test) and contract clarity (explicit debris policy). Implement the fix at the recorder. Do not touch the resolver or any playback code** — debris visibility is bound to parent visibility, so the existing resolver behavior (return false on unresolvable anchor → don't render) is already the correct answer.

---

## Decisions (confirmed by user 2026-05-07)

1. **Harness location:** `Source/Parsek.Tests` (xUnit). Deterministic mocks where they can be added cheaply.
2. **Parent unresolvable at playback (parent destroyed, loop-rejected, missing):** existing resolver behavior — return false → debris ghost doesn't render that frame. **No new fallback code.** Decision §7 below explains why.
3. **Cascade cap:** stays at `MaxRecordingGeneration = 1`. Secondary debris remains untracked.
4. **Step 4 cleanups:** defer to a separate PR after the debris fix lands.
5. **Recording-time distance source for `ShouldUseRelativeFrame`:** Option C — when `recording.AnchorRecordingId != null`, always record Relative, skip the 2300/2500m hysteresis at recording time. Simplest, fewest moving parts.
6. **Primary bug surface:** focused-vessel debris (booster decouples during ascent / staging). Background-vessel debris is secondary. This means `ParsekFlight.CreateBreakupChildRecording` is the **highest-priority** of the three creation sites — Step 3b prioritizes it.
7. **Debris visibility is bound to parent visibility (v6).** Debris is small and only meaningful within ~2300 m. If the parent isn't being rendered (too far, destroyed, loop-rejected), the debris doesn't need to either. This is why no playback fallback is needed: the existing "resolver returns false on unresolvable anchor → don't render" path is correct. Loop-debris and control-transfer therefore have no special cases. Contract is simply `child.AnchorRecordingId = parent.RecordingId` for any debris child.

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

Today's playback already renders a vessel ghost together with its debris children — they appear visually linked. Two consequences flow from this:

1. **What's wrong** is the *spatial data*: the debris's anchor at sample time was "nearest eligible vessel," which is usually but not always the parent. So the visually-linked rendering shows debris in the wrong place when the recorded anchor wasn't the parent. The contract below makes the recorded data match what the visual link already implies.
2. **What's right** is the visibility coupling: debris visibility is bound to parent visibility. Debris is small (only meaningful within ~2300 m) and exists *as part of* the parent's visual narrative. If the parent isn't rendering (too far, destroyed, loop-rejected), the debris doesn't need to either. **This is why the contract needs no playback-side fallback** — the existing resolver behavior (return false on unresolvable anchor → don't render) is exactly the right answer.

### Proposed contract

> **Debris is anchored to its parent recording for its entire lifetime.**
>
> When a split produces a child recording marked `IsDebris = true`:
>
> 1. The child's `Recording.AnchorRecordingId` (a new top-level field) is set to the parent's `RecordingId` at registration time. No special cases.
> 2. When `AnchorRecordingId != null`, every Relative `TrackSection` written into the child uses that anchor — the per-frame nearest-anchor search is **skipped** for those recordings.
> 3. **Recording-time:** Option C (per Decision §5). Always record Relative. The 2300/2500m hysteresis is bypassed at recording time — the debris is always-Relative-to-parent for its entire lifetime.
> 4. **Playback-time:** no new code path. If the parent's pose is unresolvable at the requested UT (parent destroyed, parent superseded and Re-Fly walk-back returns nothing, parent rejected as loop anchor), the existing resolver returns false and the debris ghost doesn't render for that frame. This is **the desired behavior** — debris visibility is bound to parent visibility.
> 5. The cascade cap (`MaxRecordingGeneration = 1`) stays — secondary debris (debris of debris) is still not recorded.

### Edge cases this contract addresses

| Edge case | Current behavior (bug) | Proposed behavior |
|-----------|------------------------|-------------------|
| Booster decouples, drifts away | Anchor flips to whatever is nearest, then to Absolute past 2500m → ghost can teleport when the late-life "anchor" is some other vessel that moved | Anchor stays parent for life. Once the parent is too far to render (also true for the debris at that distance), neither renders. |
| Parent re-flied (supersede chain) | Re-Fly resolver chases anchor through chain, but also through any other anchor the debris picked up during sample time → unpredictable | Anchor is always parent; resolver only chases parent supersedes. |
| Debris of debris | Silently dropped (cascade cap) | Still silently dropped — same behavior, but now explicitly contracted. |
| Parent destroyed mid-debris-life | Debris ghost may continue rendering with stale anchor pose | Resolver returns false → debris stops rendering. Matches design intent: debris exists as part of parent's visual narrative. |
| **Parent recording has `LoopAnchorVesselId != 0`** | Resolver rejects loop recording as anchor → debris's nearest-anchor fallback may pick something wrong | Resolver still rejects → debris stops rendering. No special case in the contract. Same outcome as parent-destroyed: bound to parent visibility. |
| **Cross-tree anchor** | Today's `IsRecordingAnchorDAGOrderEligible` allows cross-tree | Debris is always same-tree (parent is by construction). Contract is no-op for this case but harness scenario should verify. |

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

**Slicing:** Step 3 splits into two PRs (3a, 3b). The original plan had a third PR (3c) for a resolver-side fallback, but v6 dropped that — debris visibility is bound to parent visibility, so the existing "resolver returns false → don't render" path is the correct behavior when the parent is unresolvable. No new playback code is needed.

### 3a. Schema (PR 3a)

- Add `Recording.AnchorRecordingId` (string, default null).
- ConfigNode codec **write** in `RecordingTreeRecordCodec.SaveRecordingInto` (alongside the existing `IsDebris` write at line 215 area).
- ConfigNode codec **read** in the load path adjacent to `RecordingTreeRecordCodec.cs:744` (`isDebris` load).
- Format-version constant + bump per "Format version" section above.
- Load-time default-to-null on legacy versions (recordings with `RecordingFormatVersion < 12` get `AnchorRecordingId = null` and continue using legacy nearest-anchor at recording time + legacy shadow at playback).
- Save/load round-trip tests.
- **No recorder change. Field is unused. Bisect-safe.**

### 3b. Recorder (PR 3b) — primary fix, load-bearing

**This is the load-bearing PR — the entire fix.** v6 dropped the planned resolver fallback (PR 3c was eliminated) because debris visibility is bound to parent visibility (Decision §7). The dominant observed bug ("debris ghost teleports / desyncs") is fixed entirely by writing the *correct* `anchorRecordingId` into the section at recording time. Once the section's anchor is the parent recording, the existing playback path produces correct visuals when the parent is rendered, and produces "no render" when the parent isn't — which is the right behavior.

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

When `recording.AnchorRecordingId != null`, the recorder always writes Relative sections — no distance computation, no hysteresis at recording time. No resolver call from the recorder, no recursion concerns. At playback, the existing resolver handles parent-resolvable cases correctly (parent is rendered → debris is rendered relative to it) and parent-unresolvable cases correctly (parent isn't rendered → resolver returns false → debris isn't rendered either).

### 3c. Playback (no change required)

**No new playback code.** The dominant observed bug ("debris ghost teleports / desyncs because the recorded anchor was a third party that itself moved") is fixed entirely by PR 3b — by the time playback runs, the section's `anchorRecordingId` has already been written correctly by the recorder, and the existing resolver succeeds.

The contract's only "fallback" case is "parent unresolvable at playback time" (parent destroyed, loop-rejected, missing from save). For that case, the existing resolver behavior (return false → ghost doesn't render) is exactly the right answer, because **debris visibility is bound to parent visibility** — debris is small, only meaningful within ~2300 m, and exists as part of the parent's visual narrative. If the parent isn't rendering, the debris doesn't need to either.

This means:
- No format-version gate needed for a fallback path (there is no fallback path).
- No resolver-recursion concerns for non-debris chains (the resolver is unchanged).
- No "parent's last known Absolute pose" walk-back code (not needed).
- The legacy v7 absolute-shadow path at `ParsekFlight.cs:21852` (`TryUseRelativeAbsoluteShadowFallback`) is **untouched** — pre-existing recordings that depend on it keep using it.
- The CLAUDE.md "metres-as-degrees" gotcha never gets exercised — we never read parent's Relative-section lat/lon as body-fixed.

### 3d. Re-run harness

Scenarios 4, 5, 7 hashes will change after PR 3b (the recorder change). Reset them in PR 3b with a comment: `// reset: debris parent-anchor contract introduced, see refactor-plan.md`. Scenarios 1, 2, 3, 6, 8, 9, 10 must NOT change. If any of them does, stop and investigate before merging.

For scenarios involving debris with parent unresolvable (parent destroyed, loop-rejected): the resolver returns false → no rendered position. The harness should hash this as a sentinel value (e.g. `(NaN, NaN, NaN, NaN, NaN, NaN, NaN)`) and the test expectation is that the resolver consistently produces sentinels. This verifies the "debris bound to parent visibility" property without requiring a fallback code path.

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

## Risk analysis (updated v6)

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| Harness fails to capture a positioner-level regression | **Medium-high (acknowledged gap)** | Scoped to resolver level. Document gaps. Extend harness only if a positioner-level bug actually bites; until then, accept as known limitation. |
| Step 3 misses an `IsDebris` propagation site (clones, merge, Re-Fly inheritance, optimizer split) | **High if not enumerated; bounded now** | Eight sites enumerated in §"`IsDebris` propagation surface". Each gets a unit test. Without this, cloned / merged / re-flied debris silently lose the contract. |
| **Re-Fly inheritance loses the contract** | High if `RewindInvoker.cs:954` propagation is omitted | Explicit propagation requirement at §"Secondary propagation sites". Re-Fly + debris harness scenario (#7) catches drift. |
| Helper invocation ordering wrong → first-frame sampling sees `AnchorRecordingId == null` | High if not specified | Step 3b explicit ordering: helper called **before** `tree.AddOrReplaceRecording` and `OnVesselBackgrounded`. |
| `RecordingOptimizer.CanAutoMerge` merges debris with mismatched parents | Medium | Add `AnchorRecordingId` mismatch guard at `RecordingOptimizer.cs:65` area, alongside the existing `LoopAnchorVesselId` guard. |
| Format-version bump corrupts legacy saves | Low | ConfigNode codec only. Standard load-time default-on-legacy. |
| Recording-time parent-pose resolution introduces recursion / performance issues | Eliminated by Option C | Option C in Step 3b (always Relative when contract applies, skip hysteresis at recording time) sidesteps the problem entirely. |
| Harness mock infrastructure is harder than estimated | Medium | Initial PR 1 includes `IPlaybackBody` mock setup. If it bogs down, fall back to "test resolver math against pre-computed expected outputs" without the body abstraction. |
| Debris with parent unresolvable becomes invisible (parent destroyed / loop-rejected) | **By design, not a risk** | Decision §7: debris visibility is bound to parent visibility. Existing resolver behavior (return false → don't render) is correct. No fallback code, no associated risks. |

### Risks eliminated by v6 simplification (no longer applicable)

- ~~Step 3c fallback pre-empts legacy v7 absolute-shadow path~~ — no fallback exists; v7 path untouched.
- ~~Step 3c fallback fires on non-debris recordings → breaks Re-Fly~~ — no fallback exists.
- ~~"Parent's last known pose" reads a Relative point as Absolute~~ — no walk-back code exists.
- ~~Loop-parent-with-no-Absolute debris becomes invisible (tradeoff)~~ — no longer a tradeoff; expected behavior.
- ~~Re-Fly settle gap → fallback walks back to pre-rewind pose~~ — no fallback code that could walk back.
- ~~Audit document still has stale claims~~ — fixed in v5 commit (alongside plan v5).

---

## Proposed PR sequence (v6 — 3c eliminated)

1. **PR 1 — Harness skeleton.** `IPlaybackBody` (or fake `CelestialBody`) infrastructure + scenarios 1, 2, 6, 9 (no debris, no behavior changes). Lowest risk. Establishes tripwire for non-debris paths before any contract change.
2. **PR 2 — Harness debris baselines.** Scenarios 4, 5, 7, 10 capture *current* (broken) debris behavior and same-chain continuation behavior. Lowest risk; just adds coverage.
3. **PR 3a — Schema.** Add `Recording.AnchorRecordingId` field, ConfigNode codec read+write, format version 12. Field unused. Bisect-safe.
4. **PR 3b — Recorder + helper + propagation + optimizer + hash resets.** Add `ApplyDebrisAnchorContract` helper (Recording and string overloads). Call from all three primary creation sites. Propagate through five secondary sites. Optimizer mismatch guard. Per-frame anchor write. Reset scenarios 4, 5, 7 hashes with justification. Scenarios 1, 2, 3, 6, 8, 9, 10 must remain unchanged. **Load-bearing PR.**
5. **PR 4 (optional) — Step 4 cleanups.**

Each PR independently shippable; bisect-friendly. The previous v5 plan had a separate PR 3c for a resolver fallback; v6 dropped it because debris visibility is bound to parent visibility — the existing "resolver returns false on unresolvable anchor → don't render" is exactly correct.

---

## Open questions

None remaining. All decisions confirmed by user 2026-05-07. Plan is implementation-ready pending one final sanity pass.

---

## Success criteria

- Observed debris bugs no longer reproduce on the user's known-broken cases (verified manually in-game).
- All Re-Fly tests (existing in-game + harness scenarios 6, 7) still pass.
- Scenario hashes for non-debris non-Re-Fly cases (1, 2, 3, 6, 8, 9, 10) are unchanged across PR 3b.
- All eight `IsDebris` propagation sites have unit tests proving `AnchorRecordingId` flows through.
- `RecordingOptimizer.CanAutoMerge` has explicit test rejecting two debris with mismatched `AnchorRecordingId`.
- A test exists confirming that when a debris's parent is destroyed / loop-rejected, the resolver returns false → ghost doesn't render (the design intent for "debris visibility is bound to parent visibility").
- No new tangled coupling introduced (Step 4d list is preserved).
- No changes to playback code (resolver, positioners) — only recorder, schema, optimizer.

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
- **2026-05-07, v5 (commit `8986ecb`):** Second-round Opus review found multiple gaps in v4. Document-only fixes:
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
- **2026-05-07, v6 (this revision):** User insight collapsed Step 3c entirely. Two observations:
  - Debris isn't visible past ~2300 m (small visual element, only meaningful in close range).
  - Debris visibility is bound to parent visibility — if the parent isn't rendering, the debris doesn't need to either.
  - **Consequence:** the "fallback to Absolute when parent is unresolvable / > 2500 m" logic is unnecessary. The existing resolver behavior (return false on unresolvable anchor → don't render) is exactly the right answer. PR 3c is eliminated; the recorder change in PR 3b is the entire fix.
  - Eliminated risks: format-version gate concerns, fallback firing on non-debris, "parent's last known pose" walk-back, loop-parent-invisible tradeoff, Re-Fly settle gap. None apply with no new playback code.
  - PR sequence reduced from 5 PRs to 4 (PR 1 harness skeleton, PR 2 harness debris baselines, PR 3a schema, PR 3b recorder + propagation + optimizer + hash resets).
  - Plan no longer touches the playback path at all — purely recorder + schema + optimizer changes. Smallest blast radius of any version of this plan.
