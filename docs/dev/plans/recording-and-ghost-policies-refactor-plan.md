# Recording & Ghost Rendering — Refactor Plan

Date: 2026-05-07
Last amended: 2026-05-07 (v8 — post-Opus-review-of-v7 fixes)
Branch: `claude/investigate-recording-policies-6tMZ9`
Companion documents:
- `recording-and-ghost-policies-audit-2026-05-07.md` — read-only audit of current behavior
- `fix-debris-trajectory-rendering.md` (branch `investigate-debris-trajectory-rendering`) — companion investigation with the alternative Absolute-only proposal and the retained log bundle. See §"Considered alternatives".

## Goal

Fix the observed debris-rendering bugs **without introducing regressions in the working Re-Fly path.** Re-Fly recording and rendering currently work; nothing in this plan should change Re-Fly's data, code paths, or playback dispatch in ways that affect non-debris recordings.

## Bug evidence

The dominant symptoms come from a retained investigation session captured at `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-07_0113_debris-trajectory-rendering-investigation\` (path is local to the developer's machine; the bundle includes `KSP.log`, `Player.log`, `parsek-test-results.txt`, `MMPatch.log`, save snapshots, and recording sidecars). Companion investigation document: `docs/dev/plans/fix-debris-trajectory-rendering.md` on branch `investigate-debris-trajectory-rendering`. These signals are the validation targets for the fix: after the contract lands, replaying this session should drive every count below to zero for debris recordings.

**KSP.log counts (`grep -c`):**

- 49 × `relative-anchor-unresolved: reason=anchor-out-of-recorded-range`
- 21 × `RELATIVE recorded-anchor fallback to absolute shadow`
- 5 × `Recording anchor candidates: ... live=0/1 ghost=...` for debris

**Representative evidence lines:**

```text
[Playback] RELATIVE recorded-anchor fallback to absolute shadow: recording #6 "Kerbal X Debris" recordingId=0123b753 anchorRec=b2b5215a32ba49778a8bba50c058ff56 frames=73 sectionUT=[16500.5,16524.1]
[Playback] RELATIVE recorded-anchor fallback to absolute shadow: recording #4 "Kerbal X Debris" recordingId=6213fe30 anchorRec=c2c7d56a21e44af2abb8830a94ccaaf7 frames=59 sectionUT=[16499.8,16522.1]
[Merger] MergeTree: boundary discontinuity=8147542.00m at section[2] ut=16524.10 vessel='Kerbal X Debris' prevRef=Relative nextRef=Relative dt=0.80s expectedFromVel=144.77m cause=sample-skip
[Flight] RestoreActiveTreeFromPending: in-place continuation marker swapped target rec='e1ea034b...'->'rec_7304951b...' vessel='Kerbal X'->'Kerbal X Probe' bgMapEntries=1->0
[BgRecorder] Background recording anchor candidates: pid=186071430 recordingId=d84e050b... live=0/1 ghost=7/7 total=7 | suppressed=7
[BgRecorder] RELATIVE mode entered: pid=186071430 recordingId=d84e050b... anchorRecordingId=0123b753... source=Ghost diagnosticPid=3420041107 dist=200.8m liveCandidates=0/1 ghostCandidates=7/7
```

**Two failure modes are visible:**

1. **Ordinary debris with closed-anchor coverage hole** (recording `e13b6f3f...` anchor `00964eb6...` ends at UT 1213.398, child sample at UT 1228.435). Anchor recording finalized before the child needed it; resolver falls back to `absoluteFrames` shadow on every frame. Sparse Relative sections (`maxGap=1.640s`, `maxGap=1.846s`) make the fallback noisy.

2. **Re-Fly debris loses live anchor.** `RestoreActiveTreeFromPending` removes the active Re-Fly recording from `tree.BackgroundMap` (`bgMapEntries=1->0`). `BackgroundRecorder.AddBackgroundLiveAnchorCandidates` only resolves loaded live candidates through `BackgroundMap`, so new debris from the live Re-Fly vessel sees `live=0/1 ghost=7/7` and anchors to **old pre-Re-Fly ghosts**, encoding its trajectory relative to a displaced ghost frame instead of the actual breakup site. The merger then reads those Relative offsets as world coordinates and reports megametre-scale "discontinuities" (`105148.80m`, `406011.50m`, `8147542.00m`, up to `16479040.00m`).

**Why this fix solves both:** the parent-anchor contract makes "find the anchor" a Recording-ID lookup that doesn't go through `BackgroundMap` — Re-Fly inheritance via `RewindInvoker.cs:954` carries the parent's recording ID through the supersede chain, and the resolver's existing chain-walk handles supersede successors. The closed-coverage-hole case is fixed because the parent recording has live samples for the entire window the debris exists (debris cannot outlive a parent that was unrenderable; Decision §7).

The megametre merger discontinuities are a **separate, independent bug** in `SessionMerger.ComputeBoundaryDiscontinuity` (interprets Relative anchor-local metres as world lat/lon/alt). Tracked in §"Known follow-ups" — not addressed by this plan.

## Why "isolation by adding code paths" is the wrong instinct

The two recorders are **already physically separate**: `FlightRecorder` (focused) and `BackgroundRecorder` (everything else), mutually exclusive by `tree.BackgroundMap` membership. They share two narrow seams that are correctly factored:

- `AnchorDetector.ShouldUseRelativeFrame` (`AnchorDetector.cs:311`) — frame hysteresis decision.
- `AnchorDetector.IsRecordingAnchorEligible` (`AnchorDetector.cs:190`) — recording-level eligibility, called by both recorders.
- `FlightRecorder.IsStructuralJointBreak` (`FlightRecorder.cs:1109`) — joint-break classification, called by both joint-break handlers.

The Absolute/Relative contract is decided per-section by `section.referenceFrame` — there is no scenario-aware branching anywhere in playback. **However**, the audit oversimplified by calling this a "single playback dispatch": the downstream world-position math is dispersed across `IGhostPositioner` methods (`InterpolateAndPosition`, `InterpolateAndPositionRelative`, `PositionLoop`, etc.) implemented in `ParsekFlight.cs` (~15961-16082+), with additional per-frame `referenceFrame` checks in `GhostPlaybackEngine.cs` at lines 4578, 4631, 4647. The dispatcher is *not* scenario-aware, but it is *physically dispersed*. That matters for harness scope (see Step 1 amendment) and for understanding the resolver's blast radius.

**The actual problems the bugs come from are two different things, both addressable without splitting any working code path:**

1. **Debris policy is implicit, not contractual.** It's "nearest-eligible-anchor, like every other vessel." That's an emergent property of code that wasn't written with debris in mind — there's no contract saying "debris should be anchored to its parent." Mental models around the codebase assume there is one.
2. **No regression harness on the resolver/positioning chain.** Re-Fly works today. Nothing prevents tomorrow's fix from silently breaking it.

So the strategy is: **add tripwires (regression harness, scoped to what we can test) and contract clarity (explicit debris policy). Implement the new-data fix at the recorder (PR 3b). Add a small read-only playback gate for legacy v11 debris (PR 3c) so existing broken saves render correctly without sidecar mutation.** For v12+ debris, the resolver behavior is unchanged: return false on unresolvable anchor → don't render, because debris visibility is bound to parent visibility (Decision §7).

---

## Decisions (confirmed by user 2026-05-07)

1. **Harness location:** `Source/Parsek.Tests` (xUnit). Deterministic mocks where they can be added cheaply.
2. **Parent unresolvable at playback for v12+ debris (parent destroyed, loop-rejected, missing):** existing resolver behavior — return false → debris ghost doesn't render that frame. **No new fallback code for the contract case.** Decision §7 below explains why. (Legacy v11 debris is handled separately — see Decision §9.)
3. **Cascade cap:** stays at `MaxRecordingGeneration = 1`. Secondary debris remains untracked.
4. **Step 4 cleanups:** defer to a separate PR after the debris fix lands.
5. **Recording-time distance source for `ShouldUseRelativeFrame`:** Option C — when `recording.DebrisParentRecordingId != null`, always record Relative, skip the 2300/2500m hysteresis at recording time. Simplest, fewest moving parts.
6. **Primary bug surface:** focused-vessel debris (booster decouples during ascent / staging). Background-vessel debris is secondary. This means `ParsekFlight.CreateBreakupChildRecording` is the **highest-priority** of the three creation sites — Step 3b prioritizes it.
7. **Debris visibility is bound to parent's *recorded-data* visibility (v6 framing, v7 sharpened).** Debris is small and only meaningful within ~2300 m, and exists as part of the parent's visual narrative. If the parent's *recorded data* is unresolvable at the requested UT (parent destroyed, parent finalized, parent rejected as a loop anchor by the resolver), the debris doesn't render. This is why no playback fallback is needed for v12+ debris: the existing "resolver returns false on unresolvable recorded anchor → don't render" path is correct. Contract is simply `child.DebrisParentRecordingId = parent.RecordingId` for any debris child.

   **Note on loop replay** (sharpened from v6's "bound to parent visibility"): when the parent is itself in loop replay, the parent IS rendered — but via a live-PID anchor (`Recording.LoopAnchorVesselId`), not via recorded data. The resolver rejects the parent recording as a debris anchor in this case (loop-anchor live-PID is a separate contract; see audit §2a). So the debris is invisible during the parent's loop replay even though the parent is visible. This is a deliberate trade-off, not a tautology: matching the parent's loop-replay frame would require either a live-PID anchor on the debris (separate contract that doesn't exist) or ignoring the loop-anchor rejection (would break the loop contract for non-debris). Accepted as-is — debris is short-lived (60 s TTL) so most use cases never hit a loop replay window with debris in flight; the rare cases lose visual coherence but don't render incorrectly.
8. **Field naming.** The new top-level Recording field is `DebrisParentRecordingId : string` (default null). The name encodes the contract: it is debris-only, it points to the parent recording. Rationale: the name "Anchor" is already overloaded in the codebase (`Recording.LoopAnchorVesselId`, `TrackSection.anchorRecordingId`, `RecordingAnchorCandidate`, `AnchorDetector`); a generic-sounding alternative like `Recording.AnchorRecordingId` would invite confusion at every read site. If we ever generalize to non-debris parent anchoring, a rename is cheap.
9. **Legacy v11 debris gets a retroactive playback-only fix (PR 3c).** Existing broken saves contain v11 debris recordings whose `section.anchorRecordingId` was chosen by the nearest-vessel-at-sample-time rule and is often wrong. These recordings cannot be repaired in place (sidecar mutation on read is rejected as too risky), but their `section.absoluteFrames` shadows are intact and represent the actual world position at sample time. Playback dispatch prefers `absoluteFrames` over the resolver chain whenever `recording.IsDebris && recording.DebrisParentRecordingId == null && section.referenceFrame == Relative && section.absoluteFrames` is non-empty. This fires for legacy v11 debris only — for v12+ debris, `DebrisParentRecordingId` is always set so the gate is skipped and Decision §7 holds. Reasoning: the user has existing broken saves; without this, "fix" means "stop creating new broken data" and the existing data stays broken until overwritten.
10. **Parent goes on-rails → end debris recording.** When a debris recording's parent vessel transitions to `packed` / `!loaded` (mid-debris-life timewarp / scene boundary), the debris recording is finalized via the existing `EndDebrisRecording` path. Implementation hooks into `CheckDebrisTTL` (`BackgroundRecorder.cs:1191`); see Step 3b §"Parent on-rails" for details. Composes with Decision §7 — the same "debris bound to parent visibility" rule, applied at recording time instead of playback.

   **Parent-continuation propagation:** the `BackgroundRecorder.cs:673` parent-continuation site creates a new Recording when a parent vessel itself is continued post-split. Per the secondary-propagation enumeration, that new continuation inherits `DebrisParentRecordingId` from `parentRec`. If the inherited anchor (the original parent of the original parent) has since ended, the on-rails hook in `CheckDebrisTTL` ends the continuation too — the lookup at the loop entry (`tree.Recordings.TryGetValue(child.DebrisParentRecordingId, out parentRec)`) returns false or finds an ended recording, and the defensive end-the-debris path triggers. So Decision §10 transitively covers continuation chains.
11. **Re-Fly settle gap → suppress debris sampling while parent is suppressed.** During the ~1–2 s post-load Re-Fly settle window, the focused vessel's `FlightRecorder` blocks trajectory writes via private `ShouldSuppressReFlyPostLoadTrajectoryWrite(double ut, string source)`. Debris created during that window mirrors the parent: skip the per-frame Relative sample. The debris recording's first emitted Relative section starts at parent-settle-release UT, anchored to parent's live pose at that UT. Net effect: brand-new debris created during a Re-Fly load is invisible for ~1–2 s, then appears. Acceptable per the visibility-bound-to-parent contract.

   Mechanism (the predicate is private to `FlightRecorder` instance state; `BackgroundRecorder` cannot call it directly): expose a small static accessor — e.g. `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(string recordingId)` — that returns `true` when the active recorder's `reFlyPostLoadSettleActive == true` AND `reFlyPostLoadSettleRecordingId == recordingId`. The settle is exclusive to the focused-vessel `FlightRecorder` (only one settle session active at a time, set in `BeginReFlyPostLoadSettle` around `FlightRecorder.cs:5667`), so the accessor returns false when no settle is active or when the queried recording isn't the settle target. `BackgroundRecorder` consults this accessor for parent-suppression checks; for background-vessel debris (parent is itself a non-focused background recording, never the settle target), the accessor returns false and debris sampling proceeds normally. See Step 3b §"Re-Fly settle window" for the call site.

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
2. **What's right** is the visibility coupling: debris visibility is bound to parent visibility. Debris is small (only meaningful within ~2300 m) and exists *as part of* the parent's visual narrative. If the parent isn't rendering (too far, destroyed, loop-rejected), the debris doesn't need to either. **This is why v12+ debris needs no playback-side fallback** — the existing resolver behavior (return false on unresolvable anchor → don't render) is exactly the right answer. The separate PR 3c playback gate addresses *legacy v11 debris in existing saves*, not the v12+ contract.

### Proposed contract

> **Debris is anchored to its parent recording for its entire lifetime (v12+).**
>
> When a split produces a child recording marked `IsDebris = true` in v12+ code:
>
> 1. The child's `Recording.DebrisParentRecordingId` (a new top-level field) is set to the parent's `RecordingId` at registration time. No special cases.
> 2. When `DebrisParentRecordingId != null`, every Relative `TrackSection` written into the child uses that anchor — the per-frame nearest-anchor search is **skipped** for those recordings.
> 3. **Recording-time:** Option C (per Decision §5). Always record Relative. The 2300/2500m hysteresis is bypassed at recording time — the debris is always-Relative-to-parent for its entire lifetime.
> 4. **Playback-time for v12+ debris:** no new code path. If the parent's pose is unresolvable at the requested UT (parent destroyed, parent superseded and Re-Fly walk-back returns nothing, parent rejected as loop anchor, parent on-rails), the existing resolver returns false and the debris ghost doesn't render for that frame. This is **the desired behavior** — debris visibility is bound to parent visibility.
> 5. **Playback-time for legacy v11 debris** (`IsDebris == true && DebrisParentRecordingId == null`, per Decision §9): playback prefers `section.absoluteFrames` over the resolver chain. This is a small, version-aware playback adjustment that retroactively fixes existing broken saves. Spelled out in Step 3c.
> 6. The cascade cap (`MaxRecordingGeneration = 1`) stays — secondary debris (debris of debris) is still not recorded.

The contract is **two-pronged**: (a) v12+ recorder writes correct data going forward; (b) v11 playback prefers the persisted `absoluteFrames` shadow that already represents the actual world position at sample time. Both prongs deliver visible improvement on the user's known-broken cases — without (b), users with existing broken saves see no change until they overwrite all old debris.

### Edge cases this contract addresses

| Edge case | Current behavior (bug) | Proposed behavior |
|-----------|------------------------|-------------------|
| Booster decouples, drifts away | Anchor flips to whatever is nearest, then to Absolute past 2500m → ghost can teleport when the late-life "anchor" is some other vessel that moved | Anchor stays parent for life. Once the parent is too far to render (also true for the debris at that distance), neither renders. |
| Parent re-flied (supersede chain) | Re-Fly resolver chases anchor through chain, but also through any other anchor the debris picked up during sample time → unpredictable | Anchor is always parent; resolver only chases parent supersedes. |
| **New debris created during Re-Fly** | `RestoreActiveTreeFromPending` excludes provisional from `BackgroundMap` (`bgMapEntries=1->0`). New debris sees `live=0/1 ghost=N/N` and anchors to old pre-Re-Fly ghosts → renders at displaced pre-Re-Fly position. | Recorder writes `DebrisParentRecordingId = provisional.RecordingId` directly. No `BackgroundMap` lookup needed. Resolver's existing chain-walk handles supersede. |
| Debris of debris | Silently dropped (cascade cap) | Still silently dropped — same behavior, but now explicitly contracted. |
| Parent destroyed mid-debris-life | Debris ghost may continue rendering with stale anchor pose | Resolver returns false → debris stops rendering. Matches design intent: debris exists as part of parent's visual narrative. |
| **Parent goes on-rails / packed mid-debris-life** | Debris keeps recording Relative against an on-rails parent that has no live world pose to sample against. Resulting Relative offsets are stale or invalid. | Per Decision §10: `CheckDebrisTTL` ends the debris recording when parent is `packed` or `!loaded`. `EndDebrisRecording` flushes the existing data; debris stops growing. Symmetric with parent-destroyed. |
| **Re-Fly settle window** (debris created during ~1–2 s post-load suppression) | Parent's recording is suppressed during settle. New debris would write Relative sections referencing parent UTs with no recorded data → resolver fails for entire window. | Per Decision §11: recorder skips per-frame Relative sample for debris while parent is suppressed (same `ShouldSuppressReFlyPostLoadTrajectoryWrite` predicate). First emitted Relative section starts at parent-settle-release UT. Brand-new debris invisible for ~1–2 s, then appears. |
| **Parent recording has `LoopAnchorVesselId != 0`** (parent is in loop replay) | Resolver rejects loop recording as anchor → debris's nearest-anchor fallback may pick something wrong | Resolver still rejects → debris stops rendering. **Deliberate trade-off, NOT "bound to parent visibility":** the parent IS visible during loop replay (via live-PID anchor), but the debris isn't, because matching the loop's live-PID anchor would require a separate live-PID contract for debris that we're not introducing. Acceptable because debris is short-lived (60 s TTL) — rare to hit a loop replay with debris in flight. See Decision §7 sharpening. |
| **Cross-tree anchor** | Today's `IsRecordingAnchorDAGOrderEligible` allows cross-tree | Debris is always same-tree (parent is by construction). Contract is no-op for this case but harness scenario should verify. |
| **Legacy v11 debris in existing saves** | `section.anchorRecordingId` was chosen by nearest-vessel-at-sample-time and is often wrong. Resolver "succeeds" but produces wrong pose. Today: 21× `RELATIVE recorded-anchor fallback to absolute shadow` warnings + 49× `anchor-out-of-recorded-range` per investigation session. | PR 3c playback gate: `recording.IsDebris && recording.DebrisParentRecordingId == null` prefers `section.absoluteFrames` over the resolver. Retroactively fixes the broken saves without mutating sidecars. |

### `IsDebris` propagation surface

The plan must propagate `Recording.DebrisParentRecordingId` through **every site that propagates `IsDebris`**. Eight sites total — three primary creation sites, five secondary propagation sites. Complete enumeration:

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
- `RecordingTreeRecordCodec.cs:744` — load. Must default `DebrisParentRecordingId = null` on legacy versions; explicit symmetric write at `RecordingTreeRecordCodec.cs:215` for new field.

**Implementation strategy:** introduce a single helper `ApplyDebrisAnchorContract` (Step 3b) and call it adjacent to every primary `IsDebris = true` assignment. For secondary copy sites, propagate the new field on the same line: `dest.DebrisParentRecordingId = src.DebrisParentRecordingId;` immediately after each `dest.IsDebris = src.IsDebris;`.

**Unit test obligation (Step 1 / harness):** every numbered site above gets a unit test that exercises the propagation. If the harness scenarios capture broken behavior at any site, the propagation isn't complete.

### What *changes* at recording time

- New field: `Recording.DebrisParentRecordingId` (string, default null), persisted in ConfigNode.
- All three creation sites set it for `IsDebris` children. No exceptions.
- **Per-frame sampling, periodic path** (`BackgroundRecorder.OnBackgroundPhysicsFrame` → `ApplyBackgroundRelativeOffset` at line 1780): when `recording.DebrisParentRecordingId != null`, force Relative section, write `section.anchorRecordingId = recording.DebrisParentRecordingId`, skip the candidate-list / nearest-search.
- **Per-frame sampling, structural-event seam** (`BackgroundRecorder.OnBackgroundPhysicsFrame` → `ApplyBackgroundRelativeOffset` at line 5460, called from the structural-event snapshot path): same treatment. **Critical:** the structural-event path bypasses `UpdateBackgroundAnchorDetection`, so the debris-anchor enforcement must apply directly in `ApplyBackgroundRelativeOffset` (or its `*ForAnchorPose` callee), not only in the periodic anchor-detection branch. Per investigation §"Phase 1 §2".
- **Initial seed point.** `RegisterChildRecordingsFromSplit` builds the seed via `CreateAbsoluteTrajectoryPointFromVessel` and stores it in `pendingInitialTrajectoryPoints` for `OnVesselBackgrounded` to consume one frame later. The seed is "Absolute" only in the data sense (its `latitude/longitude/altitude` fields are body-fixed). For a debris recording with `DebrisParentRecordingId != null`, the seed is converted to Relative-to-parent **before** it lands in the first track section: open the first section as `ReferenceFrame.Relative` with `anchorRecordingId = parent.RecordingId`, then run the existing `ApplyBackgroundRelativeOffsetForAnchorPose` on the seed point (mutating `latitude/longitude/altitude` into anchor-local metres `dx/dy/dz` via `TrajectoryMath.ComputeRelativeLocalOffset`, and `rotation` into anchor-local form via `ComputeRelativeLocalRotation`). The same helpers used for the focused recorder; no new math.
- **Recording-time anchor-pose source.** Parent's live world pose at sample UT, pulled from `Vessel.transform` via the parent recording's `VesselPersistentId` (`FlightRecorder.FindVesselByPid(parent.VesselPersistentId)`). This is a scene-state lookup, not a recording-data lookup — no recursion through `RelativeAnchorResolver` at recording time. For focused-vessel debris, parent is the focused vessel (`FlightGlobals.ActiveVessel`); for background-vessel debris, parent is another loaded background vessel in `tree.BackgroundMap`. Decision §5 ensures we never need parent's recorded pose at sample time, only its live pose.
- **`AnchorDetector.ShouldUseRelativeFrame` is bypassed for debris.** Per Decision §5 (Option C): when `recording.DebrisParentRecordingId != null`, the recorder unconditionally writes Relative — no distance computation, no hysteresis read, no resolver call. This sidesteps the recursion / performance concerns the v5 plan flagged.
- **`CheckDebrisTTL` extended to end debris when parent goes on-rails** (per Decision §10). Today the loop in `BackgroundRecorder.cs:1198-1242` ends the recording when the *debris's own* `Vessel` is `null` or `!loaded`. Extend it: when `child.DebrisParentRecordingId != null`, also resolve `parentVessel = FlightRecorder.FindVesselByPid(parent.VesselPersistentId)` and end the debris recording if `parentVessel == null || parentVessel.packed || !parentVessel.loaded`. Same exit path (`EndDebrisRecording`), distinct log line (`Debris TTL: parent on-rails or destroyed, ending recording: parentPid=... vesselPid=...`).
- **Re-Fly settle gap → skip per-frame Relative sample while parent is suppressed** (per Decision §11). In the `BackgroundRecorder` per-frame entry for a recording with `DebrisParentRecordingId != null`, before computing the Relative offset, consult the new accessor `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(parent.RecordingId)`. The accessor encapsulates the private settle state (`reFlyPostLoadSettleActive` + `reFlyPostLoadSettleRecordingId`) on the focused-vessel recorder. If suppressed, skip the sample entirely — log `RELATIVE sample suppressed (parent in Re-Fly settle): pid=... recordingId=... parentRecId=...` and return without writing a section. The debris recording's first emitted section thus starts at parent-settle-release UT, anchored to parent's live pose at that UT.

### What *doesn't* change

- The `referenceFrame`-based dispatch in `GhostPlaybackEngine` and the positioner methods. Untouched for non-debris. **Debris adds one new gate** — see Step 3c (legacy debris prefers `absoluteFrames`).
- The `RelativeAnchorResolver` chain for non-debris recordings. Untouched.
- The `RelativeAnchorResolver` chain for **v12+ debris**. Walks the parent recording exactly like any other anchor; no new resolver behavior required for the contract case.
- Re-Fly walk-back for non-debris. Untouched.
- Non-debris background vessels. Untouched. (Their existing closed-anchor coverage holes are tracked in §"Known follow-ups" — not addressed by this plan.)
- The cascade cap.
- The `absoluteFrames` shadow continues to be written by the recorder for v12+ debris (the v7+ format already does this for every Relative section via `state.currentTrackSection.absoluteFrames`). The shadows are insurance — they feed PR 3c's playback rule for legacy debris, and they're still useful for `IncompleteBallisticSceneExitFinalizer` / merger paths.

### Format version

The new top-level `Recording.DebrisParentRecordingId` is written by `RecordingTreeRecordCodec.SaveRecordingInto` (ConfigNode codec). The binary `.prec` codec is **not affected** — it stores trajectory data (points, sections, orbit segments, events), not top-level Recording fields.

- ConfigNode codec: bump `RecordingFormatVersion` to 12, add `DebrisParentRecordingFormatVersion = 12 = CurrentRecordingFormatVersion` named constant in `RecordingStore.cs:57-65`.
- Binary codec: **no version bump** (the per-section `anchorRecordingId` field already exists since v11).
- Load-time: legacy recordings get `DebrisParentRecordingId = null` and use legacy nearest-anchor search.

---

## Step 3 — Implement the debris contract

**Slicing:** Step 3 is three PRs (3a schema, 3b recorder, 3c legacy-debris playback gate). The v6 revision had eliminated 3c entirely on the basis that "no playback code is needed"; v7 reintroduces a *different* 3c — not a fallback for unresolvable parents, but a small playback gate that retroactively fixes legacy v11 debris in existing saves. See Decision §9 and the §"Bug evidence" investigation for the rationale.

### 3a. Schema (PR 3a)

- Add `Recording.DebrisParentRecordingId` (string, default null).
- **Add `string DebrisParentRecordingId { get; }` to `IPlaybackTrajectory`** (`IPlaybackTrajectory.cs`, adjacent to existing `bool IsDebris` at line 70). The interface is the deliberate engine/recording isolation seam (`GhostPlaybackEngine` accesses trajectories only through it — see `IPlaybackTrajectory.cs` doc comment and CLAUDE.md §`GhostPlaybackEngine.cs`). Adding a property is a small, non-breaking change to the seam — the implementer (`Recording`) already exposes the new field as a public property by virtue of the schema change. PR 3c needs interface access to gate on it (the call sites have `IPlaybackTrajectory traj` in scope, not `Recording`).
- ConfigNode codec **write** in `RecordingTreeRecordCodec.SaveRecordingInto` (alongside the existing `IsDebris` write at line 215 area). **Conditional**: only write the line when `DebrisParentRecordingId != null`, mirroring how `IsDebris` is conditionally written. This keeps non-debris recordings byte-identical on disk across the upgrade.
- ConfigNode codec **read** in the load path adjacent to `RecordingTreeRecordCodec.cs:744` (`isDebris` load). On legacy `RecordingFormatVersion < 12`, the field defaults to null (Decision §9 behavior — PR 3c gate fires).
- Format-version constant + bump per "Format version" section above.
- Load-time default-to-null on legacy versions (recordings with `RecordingFormatVersion < 12` get `DebrisParentRecordingId = null` and continue using legacy nearest-anchor at recording time; PR 3c gate handles their playback).
- Save/load round-trip tests covering: (a) v12+ debris with field set, save/load preserves; (b) legacy v11 debris loaded into v12 code, field is null; (c) non-debris recording, field never written to disk.
- **No recorder change. Field is unused at recording-time. Bisect-safe.**

### 3b. Recorder (PR 3b) — primary fix for new data

**This is the load-bearing PR for new data.** Writing the *correct* `anchorRecordingId` into the section at recording time fixes the dominant observed bug ("debris ghost teleports / desyncs") for any debris recorded after this PR lands. PR 3c is the companion that delivers the same fix retroactively for users with existing broken saves — without 3c, "fix" means "stop creating new broken data" and existing data stays broken until overwritten.

Once the section's anchor is the parent recording, the existing playback path produces correct visuals when the parent is rendered, and produces "no render" when the parent isn't — which is the right behavior per Decision §7.

#### Helper

Introduce two overloads (one Recording-typed, one string-typed for the static factory):

```
internal static void ApplyDebrisAnchorContract(Recording child, Recording parent)
{
    if (!child.IsDebris) return;
    child.DebrisParentRecordingId = parent?.RecordingId;
}

internal static void ApplyDebrisAnchorContract(Recording child, string parentRecordingId)
{
    if (!child.IsDebris) return;
    child.DebrisParentRecordingId = parentRecordingId;
}
```

#### Primary creation sites — invocation and ordering

1. `ParsekFlight.CreateBreakupChildRecording` (`ParsekFlight.cs:5103`) — **adds a parameter**: the function does not currently receive parent-recording information (its parameters are `tree, breakupBp, pid, vessel, isDebris, fallbackName, fallbackSnapshot, fallbackTrajectoryPoint, parentGeneration`). Add `string parentRecordingId = null` (default null for non-breaking signature change). Both call sites (`ParsekFlight.cs:5497` for the controlled path, `ParsekFlight.cs:5552` for the debris path) have `activeRec` in scope and already pass `parentGeneration: activeRec.Generation` — extend each call to also pass `parentRecordingId: activeRec.RecordingId`. Inside `CreateBreakupChildRecording`, call `ApplyDebrisAnchorContract(childRec, parentRecordingId)` immediately after the `new Recording { ... }` initializer at line 5113 (before the trim at line 5143). Alternative read-from-breakupBp approach (`breakupBp.ParentRecordingIds[0]`) is rejected: the breakup branch point can have multiple parent recording IDs in chain-merge cases, and the focused recording is unambiguously the right anchor — passing it explicitly avoids that ambiguity.
2. `BackgroundRecorder.RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`) — **ordering matters** (per review §S3). Today the code at lines 1097-1115 registers the recording in the tree and initializes sampling state *before* setting `IsDebris = true` at line 1115. The helper must be moved earlier so `DebrisParentRecordingId` is set **before** `tree.AddOrReplaceRecording(child)` (line 1097) and before `OnVesselBackgrounded(...)` (line 1108). Concretely: refactor lines 1097-1129 so the `IsDebris` and `DebrisParentRecordingId` assignments happen at the top of the per-child loop iteration, then registration, then sampling init.
3. `BackgroundRecorder.BuildBackgroundSplitBranchData` (`BackgroundRecorder.cs:1143`) — call helper inside the loop at lines 1163-1180, immediately after the constructor sets `IsDebris`.

#### Secondary propagation sites

Per the IsDebris-propagation enumeration above, also propagate at:

- `Recording.cs:569` (`ApplyPersistenceArtifactsFrom`): add `DebrisParentRecordingId = source.DebrisParentRecordingId;` immediately after the `IsDebris` line.
- `SessionMerger.cs:135`: add `merged.DebrisParentRecordingId = srcRec.DebrisParentRecordingId;`.
- `RewindInvoker.cs:954`: add `provisional.DebrisParentRecordingId = inheritFrom.DebrisParentRecordingId;`. Re-Fly safety depends on this.
- `RecordingOptimizer.cs:931`: add `second.DebrisParentRecordingId = original.DebrisParentRecordingId;`.
- `BackgroundRecorder.cs:673` (parent-continuation): add `DebrisParentRecordingId = parentRec.DebrisParentRecordingId,` to the object initializer.

#### Optimizer changes (per review §S5)

- `RecordingOptimizer.CanAutoMerge` (`RecordingOptimizer.cs:26`): after the existing `LoopAnchorVesselId` mismatch guard at line 65, add **two** new mismatch guards:
  - `if (a.IsDebris != b.IsDebris) return false;` — defends against contract corruption where one half of a previously-split recording lost the field. Auto-merge only operates on consecutive chain segments of the same vessel, so this case shouldn't normally arise; the guard is one-line insurance.
  - `if (a.DebrisParentRecordingId != b.DebrisParentRecordingId) return false;` — two debris with different parents cannot auto-merge.
- `SplitAtSection` (`RecordingOptimizer.cs:931`): the `original` half is mutated in place and retains `IsDebris` and `DebrisParentRecordingId` automatically; only the `second` half (a fresh `Recording`) needs the explicit `second.DebrisParentRecordingId = original.DebrisParentRecordingId;` line, alongside the existing `second.IsDebris = original.IsDebris;`. Add an assertion test that the `original` half retains the field, so a future refactor that reconstructs `original` as a new object (rather than mutating in place) doesn't silently drop the contract.

#### Per-frame anchor write

In `BackgroundRecorder.ApplyBackgroundRelativeOffset` (called from `OnBackgroundPhysicsFrame` at line 1780, periodic sampling): when `recording.DebrisParentRecordingId != null`, set `section.anchorRecordingId = recording.DebrisParentRecordingId` and skip the candidate-list / nearest-search.

#### Structural-event seam (per investigation §Phase 1 §2)

`BackgroundRecorder.ApplyBackgroundRelativeOffset` is **also** called directly from the structural-event snapshot path at line 5460, bypassing `UpdateBackgroundAnchorDetection`. The debris-anchor enforcement must apply at both call sites: the helper that sets `section.anchorRecordingId` for debris must live inside `ApplyBackgroundRelativeOffset` (or its `*ForAnchorPose` callee), not only in the periodic anchor-detection branch. Without this, structural events for debris write Relative sections with whatever stale `state.currentAnchorRecordingId` was set in `state` — exactly the bug we're fixing.

#### Initial seed point coordinate transform

`RegisterChildRecordingsFromSplit` builds the seed point via `CreateAbsoluteTrajectoryPointFromVessel` (body-fixed `lat/lon/alt`) and stores it in `pendingInitialTrajectoryPoints[child.VesselPersistentId]` for `OnVesselBackgrounded` to consume one frame later. For debris with `DebrisParentRecordingId != null`, the seed must be transformed from body-fixed to anchor-local **before it enters the first track section**. The transform reuses `ApplyBackgroundRelativeOffsetForAnchorPose` (`BackgroundRecorder.cs:3832`), but that helper requires a fully-initialized `BackgroundVesselState` plus an `AnchorPose`, neither of which exists at `RegisterChildRecordingsFromSplit` time (state is created inside `OnVesselBackgrounded` one frame later).

**Call order** (the orchestration the implementer needs to set up — this is where the implementation does have non-trivial work):

1. `RegisterChildRecordingsFromSplit` continues to call `CreateAbsoluteTrajectoryPointFromVessel` and stash the body-fixed seed in `pendingInitialTrajectoryPoints` (today's behavior, unchanged).
2. `OnVesselBackgrounded` creates the `BackgroundVesselState` for the new child (today's behavior, unchanged).
3. **NEW** — immediately after state creation, when the recording has `DebrisParentRecordingId != null`:
   a. Resolve `parentVessel = FlightRecorder.FindVesselByPid(parent.VesselPersistentId)` — the live parent vessel.
   b. If `parentVessel == null` (parent destroyed in the same frame), abandon the transform: the recording is going to be ended on the next `CheckDebrisTTL` tick anyway. Skip writing any initial section. (Defensive — should be rare since the joint break that produced this debris just happened.)
   c. Build an `AnchorPose` from `parentVessel.transform.position` and `parentVessel.transform.rotation` at `seed.ut`.
   d. Set `state.currentAnchorRecordingId = recording.DebrisParentRecordingId`, `state.currentAnchorPid = parentVessel.persistentId`, `state.isRelativeMode = true`. (These mirror the focused recorder's `ClearCurrentRecordingAnchor` / `SetCurrentRecordingAnchor` symmetry — confirm the helper exists or extract one.)
   e. Open the first `TrackSection` as `ReferenceFrame.Relative` with `anchorRecordingId = recording.DebrisParentRecordingId`.
   f. Run `ApplyBackgroundRelativeOffsetForAnchorPose(state, ref seed, parentVessel, anchorPose, ...)` to mutate the seed's `latitude/longitude/altitude` into anchor-local `(dx, dy, dz)` and `rotation` into anchor-local form. The helper also sets `section.anchorRecordingId` via `ApplyBackgroundCurrentAnchorToTrackSection`.
   g. Append the (now-Relative) seed as the first frame.
4. Subsequent per-frame samples flow through the existing `OnBackgroundPhysicsFrame` → `ApplyBackgroundRelativeOffset` path with `state.isRelativeMode == true` already set; the per-frame anchor-write rule in §"Per-frame anchor write" handles the rest.

**Without this transform**, the body-fixed seed lands in a Relative section whose downstream interpretation expects metres-as-`(dx, dy, dz)` — the CLAUDE.md "metres-as-degrees" gotcha in reverse. The seed would then read as a wildly off-scale anchor offset.

**Test obligation** (Step 1 / harness + unit tests in PR 3b): a unit test that builds a debris recording with `DebrisParentRecordingId` set, drives the registration → `OnVesselBackgrounded` flow, and asserts that (a) the first track section has `referenceFrame == Relative` and `anchorRecordingId == parent.RecordingId`, (b) the first frame's `latitude/longitude/altitude` are anchor-local metres (small magnitudes like `O(1m)` in the just-decoupled case), not body-fixed degrees, and (c) the rotation is anchor-local (close to `Quaternion.Inverse(parentRot) * vesselRot`).

#### Parent on-rails hook (per Decision §10)

Extend `CheckDebrisTTL` (`BackgroundRecorder.cs:1191-1242`). The existing loop iterates `kvp` over `debrisTTLExpiry` and uses `vesselPid = kvp.Key`, looking up `Vessel v = FlightRecorder.FindVesselByPid(vesselPid)` for the debris vessel itself. The new check needs to additionally look up the parent recording and parent vessel:

```
// After the existing "v == null" / "TTL expired" / "!v.loaded" checks (which
// terminate on debris-vessel state), add a parent-state check for v12+ debris.
if (!tree.BackgroundMap.TryGetValue(vesselPid, out string childRecId)) continue;
if (!tree.Recordings.TryGetValue(childRecId, out Recording child)) continue;
if (string.IsNullOrEmpty(child.DebrisParentRecordingId)) continue; // legacy v11 — keep original behavior
if (!tree.Recordings.TryGetValue(child.DebrisParentRecordingId, out Recording parentRec))
{
    // Parent recording missing from tree — defensive: end the debris recording.
    if (expired == null) expired = new List<uint>();
    expired.Add(vesselPid);
    ParsekLog.Info("BgRecorder",
        $"Debris TTL: parent recording missing from tree, ending: " +
        $"parentRecId={child.DebrisParentRecordingId} childPid={vesselPid}");
    continue;
}
Vessel parentVessel = FlightRecorder.FindVesselByPid(parentRec.VesselPersistentId);
if (parentVessel == null || parentVessel.packed || !parentVessel.loaded)
{
    if (expired == null) expired = new List<uint>();
    expired.Add(vesselPid);
    ParsekLog.Info("BgRecorder",
        $"Debris TTL: parent on-rails or destroyed, ending recording: " +
        $"parentRecId={child.DebrisParentRecordingId} parentPid={parentRec.VesselPersistentId} " +
        $"parentLoaded={parentVessel?.loaded ?? false} parentPacked={parentVessel?.packed ?? false} " +
        $"childPid={vesselPid}");
    continue;
}
```

Distinct log lines so the existing "vessel destroyed/despawned" path remains diagnosable independently. The new branch is keyed on `child.DebrisParentRecordingId != null` — legacy v11 debris (without the field) keeps its original lifetime contract. The `tree.Recordings.TryGetValue` lookup is O(1) and the parent vessel lookup mirrors the existing `FindVesselByPid` pattern used for the debris vessel itself.

#### Re-Fly settle window hook (per Decision §11)

**Accessor to add** (PR 3b includes this): expose a static `IsReFlyPostLoadSettleActiveForRecording(string recordingId)` on `FlightRecorder` that reads the singleton focused-vessel recorder's instance state and returns true iff `reFlyPostLoadSettleActive && reFlyPostLoadSettleRecordingId == recordingId`. The accessor returns false when no recorder exists, no settle is active, or the recording ID doesn't match the settle target. Trivial implementation — five lines including the null guard.

In `OnBackgroundPhysicsFrame`'s entry for a recording with `DebrisParentRecordingId != null`, before `UpdateBackgroundAnchorDetection` / `ApplyBackgroundRelativeOffset`:

```
if (FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(recording.DebrisParentRecordingId))
{
    ParsekLog.VerboseRateLimited("BgRecorder",
        "debris-parent-settle-suppressed|" + recording.RecordingId,
        $"RELATIVE sample suppressed (parent in Re-Fly settle): pid={vesselPid} " +
        $"recordingId={recording.RecordingId} parentRecId={recording.DebrisParentRecordingId} " +
        $"sampleUT={sampleUT.ToString("F2", CultureInfo.InvariantCulture)}",
        2.0);
    return;
}
```

The debris recording's first emitted Relative section thus starts at parent-settle-release UT, anchored to parent's live pose at that UT. Net effect: brand-new debris created during a Re-Fly load is invisible for the ~1–2 s of settle, then appears. For background-vessel debris (parent is not the settle target), the accessor always returns false and the check is a no-op.

#### Recording-time frame decision (Option C, per Decision §5)

When `recording.DebrisParentRecordingId != null`, the recorder always writes Relative sections — no distance computation, no hysteresis at recording time. No resolver call from the recorder, no recursion concerns. At playback for v12+ debris, the existing resolver handles parent-resolvable cases correctly (parent is rendered → debris is rendered relative to it) and parent-unresolvable cases correctly (parent isn't rendered → resolver returns false → debris isn't rendered either). For legacy v11 debris (without the field), PR 3c handles playback separately.

### 3c. Legacy-debris playback gate (PR 3c) — retroactive fix for existing saves

**Purpose:** make existing v11 broken debris saves render correctly without mutating sidecar data. Per Decision §9.

**Rule.** In `ParsekFlight.InterpolateAndPositionRecordedRelative` (`ParsekFlight.cs:21228+`), insert a precheck **before** each of the three calls to `TryPositionGhostRecordedRelativeAt` / `TryResolveRecordedRelativeAnchorPose`. The three pre-resolver insertion points (in current main):

- **~line 21253** (pre-range branch, just before the first `TryPositionGhostRecordedRelativeAt(ghost, frames[0], ...)` call).
- **~line 21295** (single-point branch, just before `TryPositionGhostRecordedRelativeAt(ghost, before, ...)`).
- **~line 21368** (regular interpolation branch, just before `TryResolveRecordedRelativeAnchorPose(target, targetUT, out anchorPose)`).

Note the line numbers are NOT `21268/21310/21370` — those are the existing failure-branch `TryUseRelativeAbsoluteShadowFallback` calls, which stay in place as the resolver-failure fallback for non-debris and v12+ debris. The gate is inserted ABOVE the resolver attempt, not after its failure.

The gate code (in scope: `traj` is the `IPlaybackTrajectory`, `target.Section` is the current `TrackSection`, `target.SectionIndex` is its index):

```
if (traj.IsDebris
    && string.IsNullOrEmpty(traj.DebrisParentRecordingId)
    && target.Section.referenceFrame == ReferenceFrame.Relative
    && target.Section.absoluteFrames != null
    && target.Section.absoluteFrames.Count > 0)
{
    // Legacy v11 debris with broken anchor — prefer the absolute-shadow path
    // that already represents the actual world position at sample time.
    if (TryUseRelativeAbsoluteShadowFallback(
            recordingIndex, traj, retireSignalState, target,
            targetUT, ref cachedIndex, out interpResult))
        return;
}
// Fall through to the existing path (resolver-first, then shadow on resolver failure).
```

**Why a precheck and not a fallback rearrangement.** The existing call sites already use `TryUseRelativeAbsoluteShadowFallback` *after* resolver failure. For legacy v11 debris, the resolver often "succeeds" with the wrong anchor (some unrelated nearby vessel that has since moved) and never reaches the shadow path. Moving the shadow check ahead of the resolver — gated on the four-condition guard above — bypasses that wrong-anchor success case.

**Conditions of the gate, in order:**

1. `traj.IsDebris == true` — non-debris is unaffected.
2. `string.IsNullOrEmpty(traj.DebrisParentRecordingId)` — v12+ debris (which has the field set) skips this gate and uses the resolver per Decision §7. Only legacy v11 debris (where `DebrisParentRecordingId` defaulted to null on load) hits the new path.
3. `target.Section.referenceFrame == ReferenceFrame.Relative` — Absolute sections need no special handling.
4. `target.Section.absoluteFrames` non-null and non-empty — graceful no-op when shadow data is unavailable (oldest format versions that predate v7 had no shadows; rare in practice but defensive).

When all four conditions hold, the gate fires. When any condition fails, control falls through to the existing path verbatim.

**Blast radius.** Three call sites in `ParsekFlight.cs`. The gate is read-only — no mutation of recording or section state. For non-debris and v12+ debris recordings, the new code is unreachable (gate fails) so behavior is byte-identical to today's main.

**What this does NOT do:**

- Does **not** mutate sidecar files. Existing `.prec` data is untouched on disk.
- Does **not** alter the `RelativeAnchorResolver`'s behavior or recursion.
- Does **not** affect map / tracking-station rendering paths (`GhostMapPresence.cs`) — those have their own resolver call sites; if log evidence later shows the same legacy bug surfaces there, the same gate can be replicated. Currently out of scope (no log evidence of map-side debris desync in the investigation bundle).
- Does **not** apply to the `MergeTree: boundary discontinuity` megametre warnings. Those come from `SessionMerger.ComputeBoundaryDiscontinuity` interpreting Relative `lat/lon/alt` as world coordinates — a separate bug. Tracked in §"Known follow-ups".
- Does **not** ship without `traj.IsDebris` guard — applying the rule to non-debris would silently change behavior for any non-debris Relative section that has a shadow, which could mask real resolver bugs.

**Logging.** When the gate fires, emit a verbose log: `Playback: legacy debris path — preferring absoluteFrames shadow (recordingId=..., sectionIndex=..., shadowFrames=...)`. Use `ParsekLog.VerboseRateLimited` with a composite key keyed on `legacy-debris-shadow-preferred|<recordingId>|<sectionIndex>` so each (recording, section) logs at most once per the rate-limit window — mirroring the existing `RELATIVE recorded-anchor fallback to absolute shadow` log at `ParsekFlight.cs:21885-21891` (which uses a four-part key `recordingId|anchorRecordingId|sectionIndex`). A single legacy debris recording with multiple Relative sections then logs once per section, not once per frame.

### 3d. Re-run harness

Scenarios 4, 5, 7 hashes will change after PR 3b (the recorder change). Reset them in PR 3b with a comment: `// reset: debris parent-anchor contract introduced, see refactor-plan.md`. Scenarios 1, 2, 3, 6, 8, 9, 10 must NOT change. If any of them does, stop and investigate before merging.

For scenarios involving v12+ debris with parent unresolvable (parent destroyed, loop-rejected): the resolver returns false → no rendered position. The harness should hash this as a sentinel value (e.g. `(NaN, NaN, NaN, NaN, NaN, NaN, NaN)`) and the test expectation is that the resolver consistently produces sentinels. This verifies the "debris bound to parent visibility" property.

PR 3c adds two new scenarios:

- **Scenario 11 — Legacy v11 debris (`DebrisParentRecordingId == null`) with `absoluteFrames` shadow.** Construct a synthetic v11 recording with `IsDebris = true`, no `DebrisParentRecordingId`, a Relative section pointing at an unrelated vessel, and a populated `section.absoluteFrames`. Hash the resolver output across the section. Expectation: hashes match the shadow data (`absoluteFrames` body-fixed positions), NOT the resolver's wrong-anchor output.
- **Scenario 12 — Legacy v11 debris with no `absoluteFrames` shadow.** Same setup but with empty `absoluteFrames`. Expectation: gate's condition (4) fails, falls through to existing path. Hashes match the existing (pre-fix) behavior.

Both scenarios run only after PR 3c lands; they fail-loud if the gate isn't wired correctly.

---

## Step 4 — Zero-logic-change cleanups (deferred to post-fix PR)

### 4a. Constant migration to ParsekConfig (pure cosmetic)

- `BackgroundRecorder.MaxRecordingGeneration = 1` → `ParsekConfig.Recording.MaxRecordingGeneration`
- `BackgroundRecorder.DebrisTTLSeconds = 60.0` → `ParsekConfig.Recording.DebrisTTLSeconds`

### 4b. Audit-correction documentation

Already addressed in v5 / v7 plan revisions: the audit's stale claims about `AnchorEligibility` duplication and "single playback dispatch" are corrected at audit lines 29-43, 157-161, 192, 210-211, 217-218. No further audit edits required for this fix.

Future cleanup PRs touching the audit should preserve the corrections.

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

## Considered alternatives

### Absolute-only debris recording (rejected)

Companion investigation document `docs/dev/plans/fix-debris-trajectory-rendering.md` (branch `investigate-debris-trajectory-rendering`) proposes restoring the pre-`f5cf3b68` invariant: **`Recording.IsDebris` background recordings author Absolute trajectory sections only.** The argument: the regression correlates with `f5cf3b68` introducing recording-id Relative anchors for background recordings, and the pre-`f5cf3b68` Absolute-only path "looked decent."

This plan deliberately takes a different direction. Trade-offs:

| Property | Absolute-only | Parent-anchor Relative (this plan) |
|----------|---------------|-------------------------------------|
| Visual co-bubble (parent and debris within ~2300 m) | Loses the spatial relationship — debris drifts independently in world frame even when it should appear locked to parent | Preserves the spatial relationship — debris pose is anchored to parent's pose, identical to how the focused vessel renders relative to its loaded peers today |
| High-velocity atmospheric breakup (parent itself in Relative against focused vessel during ascent) | Debris is body-fixed but parent is moving in the focused vessel's frame → parent and debris drift apart visually | Both anchored to the same chain → coherent visual narrative |
| Closed-anchor coverage hole (parent finalized before debris) | No problem — debris is body-fixed, doesn't need an anchor | Solved by Decision §10 (parent on-rails / destroyed → end debris recording) |
| New debris during Re-Fly | Solved trivially (no anchor needed) | Solved by parent-anchor + `RewindInvoker.cs:954` inheritance + resolver supersede chain |
| Recording cost | One `body.GetLatitude/Longitude/Altitude` call per sample | One `FindVesselByPid` + one coordinate transform per sample. Both approaches are O(1) per sample; cost difference is negligible. |
| Existing broken saves | Migration path via "playback prefers shadow" (Phase 2 in the investigation) | Same migration path (PR 3c) — the two plans converge here |
| Conceptual clarity | "Debris is its own world-frame object" | "Debris is part of parent's visual narrative" |

The investigation's evidence (49 `anchor-out-of-recorded-range`, 21 fallback-to-shadow, 5 broken Re-Fly debris) is fixed by either approach. The deciding factor is the visual co-bubble: when a player watches a stage separation, the booster should appear locked to the main vessel during the brief window before they drift apart. Absolute-only loses that visual coherence; parent-anchor preserves it. This is consistent with how every other Relative-frame recording in Parsek works.

The investigation document remains a useful comparison artifact — its log evidence and root-cause analysis are independent of the chosen fix and inform §"Bug evidence" above.

### Eliminated by v6 (no longer applicable in v7)

The v6 revision eliminated five risks under the "no playback code change" framing. v7 reintroduces a small playback change (PR 3c, legacy-debris-only) — but the eliminated risks remain eliminated because v7's playback change is structurally different from the v5 fallback:

- ~~"Parent's last known pose" reads a Relative point as Absolute~~ — v7's gate uses `section.absoluteFrames` (which is body-fixed by construction), never reads parent's Relative-section lat/lon. CLAUDE.md gotcha not exercised.
- ~~Fallback fires on non-debris recordings → breaks Re-Fly~~ — v7's gate has `recording.IsDebris` as condition #1.
- ~~Format-version gate concerns~~ — v7's gate uses presence/absence of `DebrisParentRecordingId` (a runtime field state) instead of a version comparison.
- ~~Loop-parent-with-no-Absolute debris becomes invisible (tradeoff)~~ — for v12+ debris, still by-design invisible per Decision §7. For v11 legacy debris with shadow, renders correctly via shadow.
- ~~Re-Fly settle gap → fallback walks back to pre-rewind pose~~ — v7's recorder skips sampling during settle (Decision §11), so no settle-window data exists to walk back to.

---

## Known follow-ups not in this plan

These are issues surfaced during investigation that share scope with the debris fix but are independent and are deliberately deferred.

1. **`SessionMerger.ComputeBoundaryDiscontinuity` Relative-frame interpretation bug.** Current implementation compares `latitude/longitude/altitude` between consecutive points without checking `section.referenceFrame`. For Relative sections those fields are anchor-local metres `(dx, dy, dz)`, not world coordinates — the merger reads them as if they were lat/lon/alt and reports megametre "discontinuities" that aren't real. Source of the `8147542.00m` and `16479040.00m` warnings in the §"Bug evidence" appendix. Fix lives in `SessionMerger.cs`, independent of this plan; needs its own design pass.
2. **Non-debris Relative anchor coverage holes.** Today's `BackgroundRecorder` can write a Relative section against an anchor recording that finalizes before the section's UT range ends. At playback this produces `anchor-out-of-recorded-range` for non-debris too. Investigation Phase 3 proposes a `LiveOpen` / `PersistedResolvable` / `PersistedOutOfRange` classification at recording time. Out of scope here; non-debris vessels are not the dominant bug surface and the same fix doesn't compose with parent-anchor for debris.
3. **Parent recording deletion cascade.** When a user deletes a recording with debris children referencing it via `DebrisParentRecordingId`, the debris is orphaned (visible in recordings table, unrenderable). Decision deferred — UX pass on the deletion dialog should decide whether to cascade-delete with confirmation, hide-orphans, or warn-and-leave. Recorder/playback code is unchanged either way.
4. **Map / tracking-station rendering for legacy debris.** PR 3c's gate is in `ParsekFlight.InterpolateAndPositionRecordedRelative` (flight-scene rendering). `GhostMapPresence.cs` has its own resolver call sites for tracking-station and map markers; if log evidence later shows debris also misrenders there, replicate the gate. Deferred until log evidence justifies it.
5. **Positioner extraction from `ParsekFlight`** to enable a fuller xUnit harness (currently §4d). Re-evaluate if a positioner-level regression bites that the resolver-level harness misses.

---

## Risk analysis (updated v7)

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| Harness fails to capture a positioner-level regression | **Medium-high (acknowledged gap)** | Scoped to resolver level. Document gaps. Extend harness only if a positioner-level bug actually bites; until then, accept as known limitation. |
| Step 3 misses an `IsDebris` propagation site (clones, merge, Re-Fly inheritance, optimizer split) | **High if not enumerated; bounded now** | Eight sites enumerated in §"`IsDebris` propagation surface". Each gets a unit test. Without this, cloned / merged / re-flied debris silently lose the contract. |
| **Re-Fly inheritance loses the contract** | High if `RewindInvoker.cs:954` propagation is omitted | Explicit propagation requirement at §"Secondary propagation sites". Re-Fly + debris harness scenario (#7) catches drift. |
| Helper invocation ordering wrong → first-frame sampling sees `DebrisParentRecordingId == null` | High if not specified | Step 3b explicit ordering: helper called **before** `tree.AddOrReplaceRecording` and `OnVesselBackgrounded`. |
| **Structural-event seam writes Relative without parent-anchor enforcement** | High if not addressed | `BackgroundRecorder.cs:5460` calls `ApplyBackgroundRelativeOffset` directly from the structural-event snapshot path, bypassing `UpdateBackgroundAnchorDetection`. Step 3b §"Per-frame sampling" requires the debris-anchor enforcement to apply at both call sites (line 1780 and line 5460). Investigation §Phase 1 §2 caught this. |
| **Initial seed point lands in Relative section without coordinate transform** | High if not addressed | The seed from `CreateAbsoluteTrajectoryPointFromVessel` has body-fixed `lat/lon/alt`. If it's appended to a Relative section without running `ApplyBackgroundRelativeOffsetForAnchorPose` first, the resulting point has metres-as-degrees CLAUDE.md gotcha. Step 3b §"Initial seed point" requires the transform before the seed enters the first section. |
| `RecordingOptimizer.CanAutoMerge` merges debris with mismatched parents | Medium | Add `DebrisParentRecordingId` mismatch guard + `IsDebris` mismatch guard at `RecordingOptimizer.cs:65` area, alongside the existing `LoopAnchorVesselId` guard. |
| Format-version bump corrupts legacy saves | Low | ConfigNode codec only. Standard load-time default-on-legacy. |
| Recording-time parent-pose resolution introduces recursion / performance issues | Eliminated by Option C + Decision §11 | Option C (always Relative when contract applies, skip hysteresis at recording time) plus Decision §11 (skip sampling during parent's Re-Fly settle suppression) sidestep recursion entirely. Recording-time anchor-pose source is `Vessel.transform`, not a recording-data lookup. |
| Harness mock infrastructure is harder than estimated | Medium | Initial PR 1 includes `IPlaybackBody` mock setup. If it bogs down, fall back to "test resolver math against pre-computed expected outputs" without the body abstraction. |
| **PR 3c gate fires on a path other than `InterpolateAndPositionRecordedRelative`** | Medium | Three pre-resolver insertion points enumerated explicitly (`ParsekFlight.cs:~21253, ~21295, ~21368`). If a fourth site exists, the gate has a known gap; map-side rendering (`GhostMapPresence.cs`) is captured in §"Known follow-ups" rather than expanded into 3c's scope. |
| **PR 3c gate masks a real resolver bug for v12+ debris** | Eliminated by gate condition #2 | The gate explicitly requires `string.IsNullOrEmpty(recording.DebrisParentRecordingId)` — v12+ debris (which always has the field set) skips the gate entirely. A resolver bug for v12+ debris would surface as today, no masking. |
| **Parent on-rails / packed mid-debris-life produces stale Relative offsets** | High without §"Parent on-rails" hook | Decision §10 + Step 3b §"Parent on-rails" hook in `CheckDebrisTTL`: end the debris recording when parent transitions to `packed` / `!loaded`. Distinct log line for diagnosis. |
| **Re-Fly settle gap → debris writes Relative against suppressed parent** | High without §"Re-Fly settle window" hook | Decision §11 + Step 3b §"Re-Fly settle window" hook: skip per-frame sampling while `ShouldSuppressReFlyPostLoadTrajectoryWrite(parent.RecordingId, sampleUT)`. New debris invisible for ~1–2 s post-load, then renders correctly. |
| Debris with parent unresolvable becomes invisible (parent destroyed / loop-rejected) for v12+ recordings | **By design, not a risk** | Decision §7: debris visibility is bound to parent visibility. Existing resolver behavior (return false → don't render) is correct. |
| Legacy v11 debris in existing saves never gets fixed | **Eliminated by PR 3c** | Decision §9 + PR 3c gate retroactively resolve legacy debris via `absoluteFrames` shadow. Users with broken saves see immediate improvement on next playback. |

---

## Proposed PR sequence (v7)

1. **PR 1 — Harness skeleton.** `IPlaybackBody` (or fake `CelestialBody`) infrastructure + scenarios 1, 2, 6, 9 (no debris, no behavior changes). Lowest risk. Establishes tripwire for non-debris paths before any contract change.
2. **PR 2 — Harness debris baselines.** Scenarios 4, 5, 7, 10 capture *current* (broken) debris behavior and same-chain continuation behavior. Lowest risk; just adds coverage.
3. **PR 3a — Schema.** Add `Recording.DebrisParentRecordingId` field, ConfigNode codec read+write, format version 12. Field unused. Bisect-safe.
4. **PR 3b — Recorder + helper + propagation + optimizer + hash resets.** Add `ApplyDebrisAnchorContract` helper (Recording and string overloads). Call from all three primary creation sites. Propagate through five secondary sites. Both `ApplyBackgroundRelativeOffset` call sites (periodic line 1780 + structural-event seam line 5460). Initial seed-point coordinate transform. `CheckDebrisTTL` extension for parent-on-rails. Re-Fly settle suppression check. Optimizer mismatch guard (`DebrisParentRecordingId` and `IsDebris`). Reset scenarios 4, 5, 7 hashes with justification. Scenarios 1, 2, 3, 6, 8, 9, 10 must remain unchanged. **Load-bearing for new data.**
5. **PR 3c — Legacy-debris playback gate.** Insert the four-condition gate at `ParsekFlight.cs:~21253, ~21295, ~21368` (pre-resolver insertion points). Add scenarios 11 + 12 to harness. Read-only on recording state, no mutation. **Retroactive fix for existing v11 broken saves.**
6. **PR 4 (optional) — Step 4 cleanups.**

Each PR independently shippable; bisect-friendly. PRs 3b and 3c are intentionally separable: a problem in 3c (e.g. legacy gate misfires) can be reverted without giving up the new-data fix in 3b, and vice versa.

PR 3c is small (three call sites in one file, one read-only gate) and can ship in parallel with PR 3b once the schema (3a) is merged. Recommended order is 3a → 3b → 3c so that the harness's debris scenarios can verify both the new-data path (after 3b) and the legacy-data path (after 3c) before users see either change.

---

## Open questions

None remaining. All decisions confirmed by user 2026-05-07. Plan is implementation-ready pending one final sanity pass.

`docs/dev/todo-and-known-bugs.md` should be updated as part of PR 3c (per CLAUDE.md "Documentation Updates — Per Commit") to mark this fix as superseding the suspected active-Re-Fly-live-debris-anchor fix mentioned in the investigation document.

The CLAUDE.md format-version constant block (`RecordingStore.cs:57-65`) needs an explicit edit in PR 3a: add `DebrisParentRecordingFormatVersion = 12 = CurrentRecordingFormatVersion` and bump the `CurrentRecordingFormatVersion` line. The CLAUDE.md "Format-v11 enums" prose paragraph also needs the v12 entry. This is a same-commit concern.

---

## Success criteria

### Behavioral validation (replay the investigation session)

The success counts below assume **all three PRs (3a + 3b + 3c) have landed AND the user has either replayed an existing broken save (PR 3c is what fixes that) or recorded fresh debris (PR 3b is what fixes that).** Intermediate states do not satisfy the full criteria:

- After 3a only: field exists but unused. No behavior change. Bisect-safe.
- After 3a + 3b only: new debris recorded post-3b is correctly anchored, but old debris in existing saves still renders wrong (21× shadow-fallback warnings unchanged).
- After 3a + 3c only: legacy gate fires for **all** debris (since `DebrisParentRecordingId == null` everywhere until 3b populates it). New debris would render via the shadow path — correct visually but doesn't deliver the parent-anchor contract for new data.
- After all three: full criteria below hold.

Replaying `logs/2026-05-07_0113_debris-trajectory-rendering-investigation/` (or an equivalent fresh ascent + Re-Fly + breakup repro) and running `python scripts/collect-logs.py debris-trajectory-rendering-fix` should produce:

- **Zero** `[Parsek][WARN][Playback] RELATIVE recorded-anchor fallback to absolute shadow` lines for `IsDebris` recordings (today: 21).
- **Zero** `[Parsek][WARN][Anchor] anchor-out-of-recorded-range` for debris recordings on the new-data path (today: 49 across all sources). For legacy v11 debris in existing saves, the warning may still emit if the `absoluteFrames` shadow path also fails — but PR 3c's gate prefers the shadow before the resolver reports the warning, so the count should drop substantially.
- **Zero** `Background recording anchor candidates: ... live=0/1 ghost=N/N` for debris created during a Re-Fly session (today: 5).
- **Zero** megametre-scale `MergeTree: boundary discontinuity` warnings for debris recordings authored after PR 3b. (Pre-existing legacy debris may still trigger these via `SessionMerger.ComputeBoundaryDiscontinuity`'s independent bug — see §"Known follow-ups" #1.)
- Manual in-game confirmation: booster ghost stays visually locked to the parent vessel during the staging breakup window; new debris created during a Re-Fly load appears at the actual breakup site (not a displaced pre-Re-Fly ghost frame).

### Test coverage

- All eight `IsDebris` propagation sites have unit tests proving `DebrisParentRecordingId` flows through.
- `RecordingOptimizer.CanAutoMerge` has explicit tests rejecting (a) two debris with mismatched `DebrisParentRecordingId`, (b) a debris paired with a non-debris.
- `RecordingOptimizer.SplitAtSection` has an assertion test verifying both halves keep the parent's `DebrisParentRecordingId`.
- A test exists confirming that when a v12+ debris's parent is destroyed / loop-rejected / on-rails, the resolver returns false → ghost doesn't render (the design intent for "debris visibility is bound to parent's recorded-data visibility").
- A test exists confirming that when a v12+ debris's parent enters Re-Fly settle suppression, the recorder skips per-frame sampling for the duration. The test invokes the new `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording` accessor for the assertion path.
- **A test exists confirming the structural-event seam writes the correct anchor.** Build a v12+ debris recording with `DebrisParentRecordingId = parent.RecordingId`, set `state.currentAnchorRecordingId` to a stale unrelated value, then drive a structural-event snapshot through the path that calls `ApplyBackgroundRelativeOffset` at line 5460. Assert that the resulting `TrackSection.anchorRecordingId == parent.RecordingId` (NOT the stale value). This test specifically guards the high-impact risk row "Structural-event seam writes Relative without parent-anchor enforcement."
- A test exists confirming the initial-seed-point coordinate transform: build a debris recording with `DebrisParentRecordingId` set, run the registration → `OnVesselBackgrounded` flow with a known parent live pose, and assert that (a) the first track section is `Relative` with the correct anchor, (b) the first frame's lat/lon/alt are anchor-local metres of the right magnitude, (c) the rotation matches `Quaternion.Inverse(parentRot) * vesselRot`.
- A test exists confirming that legacy v11 debris with `absoluteFrames` shadow is rendered via the shadow (PR 3c gate fires).
- A test exists confirming that legacy v11 debris **without** `absoluteFrames` shadow falls through to the existing resolver path (PR 3c gate condition #4 fails).
- A test exists confirming that v12+ debris is **not** caught by PR 3c's gate (condition #2 fails because `DebrisParentRecordingId` is set).

### Non-goals / negative invariants

- Scenario hashes for non-debris, non-Re-Fly cases (1, 2, 3, 6, 8, 9, 10) are unchanged across PR 3b and PR 3c.
- For non-debris recordings: zero behavior change in `ParsekFlight.InterpolateAndPositionRecordedRelative` (PR 3c gate fails on condition #1).
- For v12+ debris with `DebrisParentRecordingId` set: byte-identical playback as PR 3b alone (PR 3c gate fails on condition #2).
- No new tangled coupling introduced (Step 4d list is preserved).
- No changes to the `RelativeAnchorResolver` chain. PR 3c adjusts the **caller** of the resolver, not the resolver itself.
- No mutation of `.prec` sidecar data on read or save. Existing files are byte-identical across upgrades.

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
  - **Added format-version gate** to the resolver fallback: `RecordingFormatVersion >= DebrisParentRecordingFormatVersion` (i.e. ≥12). Legacy v7 debris keeps using the existing `ParsekFlight.cs:21852` absolute-shadow fallback. The new fallback applies only to v12+ recordings. Guard is now quadruple-conjunction.
  - **Added optimizer interactions**: `RecordingOptimizer.CanAutoMerge` mismatch guard for `DebrisParentRecordingId`; `SplitAtSection` propagation already covered by the secondary-sites enumeration.
  - **Added helper string-overload** for the pure-static `BuildBackgroundSplitBranchData` factory (which takes `parentRecordingId : string`, not a `Recording`).
  - **Schema PR 3a** now explicitly covers ConfigNode read symmetry in addition to write.
  - **Documented loop-parent-with-no-Absolute-points limitation** explicitly: such debris is invisible at playback. Tradeoff vs v2's wrong-but-visible. User accepted (Decision §7).
  - **Added Re-Fly-settle-gap edge case** acknowledgement (low severity; debris is short-lived).
  - **Audit document updated in tandem**: stale "single dispatch" and "duplicated anchor candidate building" claims fixed at audit lines 29-43, 157-161, 217-218.
- **2026-05-07, v6 (commit `6f2c2db3`):** User insight collapsed Step 3c entirely. Two observations:
  - Debris isn't visible past ~2300 m (small visual element, only meaningful in close range).
  - Debris visibility is bound to parent visibility — if the parent isn't rendering, the debris doesn't need to either.
  - **Consequence:** the "fallback to Absolute when parent is unresolvable / > 2500 m" logic is unnecessary. The existing resolver behavior (return false on unresolvable anchor → don't render) is exactly the right answer. PR 3c is eliminated; the recorder change in PR 3b is the entire fix.
  - Eliminated risks: format-version gate concerns, fallback firing on non-debris, "parent's last known pose" walk-back, loop-parent-invisible tradeoff, Re-Fly settle gap. None apply with no new playback code.
  - PR sequence reduced from 5 PRs to 4 (PR 1 harness skeleton, PR 2 harness debris baselines, PR 3a schema, PR 3b recorder + propagation + optimizer + hash resets).
  - Plan no longer touches the playback path at all — purely recorder + schema + optimizer changes. Smallest blast radius of any version of this plan.
- **2026-05-07, v7 (this revision):** Integration of bug evidence and a small playback-side path for legacy data. Triggered by reviewing the companion investigation document `docs/dev/plans/fix-debris-trajectory-rendering.md` (branch `investigate-debris-trajectory-rendering`).
  - **Added §"Bug evidence"** anchored to the retained `logs/2026-05-07_0113_debris-trajectory-rendering-investigation/` bundle. Concrete counts (49 / 21 / 5), representative log lines, two failure-mode breakdowns. Establishes validation criteria.
  - **Added §"Considered alternatives"** that explicitly weighs the investigation's Absolute-only proposal against the parent-anchor Relative direction. Rejection rationale: visual co-bubble preservation; high-velocity ascent breakup coherence; recording cost. Investigation evidence remains useful as comparison material.
  - **Added §"Known follow-ups not in this plan"** listing the `SessionMerger` Relative-frame interpretation bug (megametre discontinuities), non-debris anchor coverage holes, parent-deletion cascade UX, map-side rendering for legacy debris, positioner extraction. Each tracked as independent.
  - **Reintroduced PR 3c with a different shape.** Not a fallback for unresolvable parents (v5's design, eliminated in v6) — instead, a four-condition playback-time gate that prefers `section.absoluteFrames` for legacy v11 debris (`recording.IsDebris && DebrisParentRecordingId == null && referenceFrame == Relative && absoluteFrames` non-empty). Three call sites in `ParsekFlight.cs:21268, 21310, 21370`. Read-only on recording state. Retroactively fixes existing broken saves without sidecar mutation.
  - **Renamed** the new field to `Recording.DebrisParentRecordingId` (was `Recording.AnchorRecordingId`). Reasoning: "Anchor" is overloaded in the codebase; the new name encodes the contract is debris-only. Helper name (`ApplyDebrisAnchorContract`) unchanged. Format-version constant renamed to `DebrisParentRecordingFormatVersion`.
  - **Added Decisions §8 (naming), §9 (legacy retroactive fix), §10 (parent-on-rails), §11 (Re-Fly settle gap).** §10 and §11 surfaced during code investigation: Decision §7's "no playback fallback" framing only holds at recording time if these recorder-side hooks exist; otherwise the recorder produces stale or unresolvable Relative sections.
  - **Step 3b expanded:** explicit treatment of the structural-event seam at `BackgroundRecorder.cs:5460` (per investigation §Phase 1 §2 — `ApplyBackgroundRelativeOffset` is called from two places, not one), explicit initial-seed-point coordinate transform via `ApplyBackgroundRelativeOffsetForAnchorPose` before the seed enters the first section, explicit recording-time anchor-pose source (`Vessel.transform` lookup, not resolver chain), explicit `CheckDebrisTTL` extension for parent-on-rails, explicit Re-Fly settle suppression check.
  - **Risk analysis updated** with three new rows (structural-event seam, initial seed transform, parent-on-rails) and refined existing rows. PR 3c risks bounded by the four-condition gate (no masking of resolver bugs for v12+ debris).
  - **Success criteria tightened** with concrete log-grep validation targets (zero / zero / zero / zero) tied to the investigation bundle.
  - **PR sequence back to 5** (1 harness, 2 baselines, 3a schema, 3b recorder + structural-event seam + propagation + on-rails + settle, 3c legacy gate, 4 cleanups). PR 3c is small and independently revertible.
- **2026-05-07, v8 (this revision):** Post-Opus-review-of-v7 fixes. The v7 review surfaced four hard technical errors and several smaller corrections; this revision addresses them in the plan rather than discovering them during implementation.
  - **Re-Fly settle accessor.** Decision §11 + Step 3b now spell out that `ShouldSuppressReFlyPostLoadTrajectoryWrite(double ut, string source)` is private to `FlightRecorder` instance state. PR 3b adds a public/static accessor `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(string recordingId)` that the BackgroundRecorder consults; for background-vessel debris (parent never enters settle), the accessor returns false and the check is a no-op.
  - **`CreateBreakupChildRecording` parent param.** Step 3b §"Primary creation sites #1" now specifies adding `string parentRecordingId = null` to the function signature and passing `activeRec.RecordingId` from both call sites (`ParsekFlight.cs:5497, 5552`).
  - **Seed-point conversion call order.** Step 3b §"Initial seed point coordinate transform" now spells out the seven-step orchestration: `RegisterChildRecordingsFromSplit` stashes seed → `OnVesselBackgrounded` creates state → resolve parent vessel → build AnchorPose → set state to relative mode → open Relative section → run `ApplyBackgroundRelativeOffsetForAnchorPose` → append. Adds a unit test obligation for the transform's correctness.
  - **`CheckDebrisTTL` parent-on-rails pseudocode.** Step 3b §"Parent on-rails hook" pseudocode now correctly resolves `child = tree.Recordings[tree.BackgroundMap[vesselPid]]` and `parentRec = tree.Recordings[child.DebrisParentRecordingId]` before the on-rails check. Adds a defensive branch when parent recording is missing from the tree.
  - **`IPlaybackTrajectory.DebrisParentRecordingId`.** PR 3a now adds the field to the engine/recording isolation seam (`IPlaybackTrajectory.cs` adjacent to `IsDebris` at line 70). PR 3c's gate uses `traj.IsDebris` / `traj.DebrisParentRecordingId` (the call sites have `IPlaybackTrajectory traj` in scope, not `Recording`).
  - **PR 3c gate insertion line numbers corrected.** Plan now cites the pre-resolver insertion points `21253 / 21295 / 21368` instead of the post-resolver-failure `21268 / 21310 / 21370`. Failure-branch `TryUseRelativeAbsoluteShadowFallback` calls stay in place as the fallback for non-debris and v12+ debris.
  - **Loop-debris framing sharpened in Decision §7 and edge-case table.** v6's "bound to parent visibility" was misleading because parent IS rendered during loop replay (via live-PID anchor). v8 explicitly frames the loop-debris invisibility as a deliberate trade-off (matching the loop-replay frame would require a separate live-PID contract for debris that is not introduced).
  - **Structural-event seam unit test obligation added** to §"Test coverage."
  - **Inter-PR window risk surfaced.** §"Success criteria" preface now explicitly distinguishes "after 3a only / after 3a+3b only / after 3a+3c only / after all three" — the zero-warning targets only hold after all three land AND the user replays a broken save (or records fresh debris).
  - **Parent-continuation site coverage.** Decision §10 now notes that the `BackgroundRecorder.cs:673` parent-continuation site composes transitively via the same `CheckDebrisTTL` lookup chain.
  - **Conditional codec-write.** PR 3a now requires writing `DebrisParentRecordingId` only when non-null, mirroring `IsDebris`. Non-debris recordings remain byte-identical on disk across the upgrade.
  - **Three-times-per-frame logging key composition** now mirrors the existing `RELATIVE recorded-anchor fallback to absolute shadow` pattern (composite key `legacy-debris-shadow-preferred|<recordingId>|<sectionIndex>`).
  - **Nits:** Bug-evidence bundle path made absolute and described. v6 commit hash added. Recording-cost row in §"Considered alternatives" corrected (both approaches are O(1)).
