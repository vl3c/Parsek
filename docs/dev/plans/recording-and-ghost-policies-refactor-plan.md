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

### Three child-recording creation sites (post-review)

The original plan named one. There are three:

1. `BackgroundRecorder.RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`) — registers in tree after a BG-vessel split. Calls into `BuildBackgroundSplitBranchData` for the actual `Recording` instances.
2. `BackgroundRecorder.BuildBackgroundSplitBranchData` (`BackgroundRecorder.cs:1143`) — pure-static factory that constructs the `Recording` instances. This is where `IsDebris` and `Generation` are set today.
3. `ParsekFlight.CreateBreakupChildRecording` (`ParsekFlight.cs:5103`) — focused-vessel debris path via the crash coalescer (called from coalescer-emitted breakup branch points at `ParsekFlight.cs:5497, 5552`). This is the **dominant case** for in-flight booster decouples and the original plan missed it entirely.

**All three sites must set `Recording.AnchorRecordingId` consistently.** See Step 3b for the implementation strategy (helper function vs. duplicate code).

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
- ConfigNode codec write/read for the new field.
- Format-version constant + bump per "Format version" section above.
- Load-time default-to-null on legacy versions.
- Save/load round-trip tests.
- **No recorder change. Field is unused. Bisect-safe.**

### 3b. Recorder (PR 3b) — three sites, one helper

The three child-recording creation sites have similar but not identical setup. Introduce a small helper:

```
internal static void ApplyDebrisAnchorContract(Recording child, Recording parent)
{
    if (!child.IsDebris) return;
    child.AnchorRecordingId = parent.RecordingId;
}
```

Call this helper from all three sites, **prioritizing the focused-vessel path first** (per Decision §6, this is where the observed bugs are):

1. `ParsekFlight.CreateBreakupChildRecording` (`ParsekFlight.cs:5103`) — focused-vessel debris via crash coalescer. **Highest priority — the dominant bug surface.**
2. `BackgroundRecorder.RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`) — BG-vessel split registration.
3. `BackgroundRecorder.BuildBackgroundSplitBranchData` (`BackgroundRecorder.cs:1143`) — pure-static factory called from path 2.

The pure-static `BuildBackgroundSplitBranchData` may need a parent-recording parameter (it currently takes `parentRecordingId : string`) — small refactor, unit-testable.

**Per-frame anchor write** (in `BackgroundRecorder` sampling loop): when `recording.AnchorRecordingId != null`, set `section.anchorRecordingId = recording.AnchorRecordingId` and skip the candidate-list / nearest-search.

**Recording-time frame decision (Option C, per Decision §5):** when `recording.AnchorRecordingId != null`, the recorder always writes Relative sections — no distance computation, no hysteresis at recording time. The playback fallback handles parent-far / parent-gone uniformly. No resolver call from the recorder, no recursion concerns.

### 3c. Resolver fallback (PR 3c)

The fallback case "parent unresolvable or > 2500m from offset" lives in `RelativeAnchorResolver.TryResolveRelativeSectionPose` (`RelativeAnchorResolver.cs:540-612`).

**Critical guard (per review §R3):** the function is called recursively from `TryResolveAnchorPose` (line 199). The fallback must **only** fire when:

```
recording.IsDebris == true
  && recording.AnchorRecordingId != null
  && (anchorPoseResolutionFailed || distance(parentPose, debrisOffset) > 2500m)
```

Non-debris recordings, or debris recordings without `AnchorRecordingId`, take the existing `WarnUnresolved` path. **Add explicit unit tests** that exercise non-debris Relative anchor chains and verify the fallback never fires.

Order of operations inside the resolver (per review §E7):
1. Anchor lookup.
2. Same-chain continuation lookup (line 247) — preserves chain-aware Re-Fly.
3. Re-Fly walk-back (`TryResolveActiveReFlyAnchorRecording`).
4. **Then** the new debris fallback.
5. Then `WarnUnresolved` + return false.

The "parent's last known Absolute pose" lookup uses the precise definition in Step 2 §"Defining 'parent's last known pose'."

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

## Risk analysis (updated)

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| Harness fails to capture a positioner-level regression | **Medium-high (acknowledged gap)** | Scoped to resolver level. Document gaps. Extend harness only if a positioner-level bug actually bites; until then, accept as known limitation. |
| Step 3 misses a debris registration site | **Was high in v1; medium now** | Three sites enumerated. Helper function reduces drift. Harness scenarios 4 + 5 + 7 cover all three paths. |
| Step 3c fallback fires on non-debris recordings → breaks Re-Fly | **High if guard is wrong** | Triple-conjunction guard (`IsDebris && AnchorRecordingId != null && resolution-failed`). Unit tests exercise non-debris chains and assert fallback never invoked. |
| "Parent's last known pose" reads a Relative point as Absolute | **High if not specified** | Binding definition in Step 2: walk back to last Absolute point only. Refuse to fallback if no Absolute point exists. Do NOT reinterpret Relative lat/lon as body-fixed. |
| Loop-parent debris becomes unrenderable | Defensive; severity bounded by the fallback | If it occurs, resolver's existing loop-anchor rejection (`RelativeAnchorResolver.cs:154`) drops through to the new debris fallback, which renders Absolute. No special-case code in the recorder. The contract doesn't depend on assumptions about which code paths produce loop recordings. |
| Format-version bump corrupts legacy saves | Low | ConfigNode codec only. Standard load-time default-on-legacy. |
| Recording-time parent-pose resolution introduces recursion / performance issues | Medium | Option C in Step 3b (always Relative when contract applies, skip hysteresis at recording time) sidesteps the problem entirely. Sign-off needed. |
| Harness mock infrastructure is harder than estimated | Medium | Initial PR 1 includes `IPlaybackBody` mock setup. If it bogs down, fall back to "test resolver math against pre-computed expected outputs" without the body abstraction. |

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
- The triple-conjunction guard in Step 3c has explicit non-debris unit tests proving the fallback can't fire there.
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
- **2026-05-07, v4 (this revision):** Stripped Gloops-specific reasoning. User flagged that Gloops module separation and dev is incomplete; the plan should not depend on assumptions about which code paths produce loop recordings. The loop-parent simplification stands but is now justified solely by the resolver-rejection + new-fallback composition — Gloops-independent. No behavior or schema changes vs v3.
