# Recording & Ghost Rendering — Refactor Plan

Date: 2026-05-07
Last amended: 2026-05-07 (v12 — explicit-interface bridge for IPlaybackTrajectory + audit §2f residual self-anchor)
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

   **Parent-continuation propagation (only relevant if cascade cap raised).** The `BackgroundRecorder.cs:673` parent-continuation site creates a new Recording when a parent vessel itself is continued post-split. With today's `MaxRecordingGeneration = 1`, this site never creates a *debris* continuation — debris-of-debris is dropped at the cap, so no Generation=2 debris recording exists to inherit `DebrisParentRecordingId`. The propagation line `IsDebris = parentRec.IsDebris` at line 673 will copy `false` for non-debris continuations and never copy `true` for debris continuations. So under current code, the chain composition this paragraph describes is unreachable.

   The propagation line for `DebrisParentRecordingId` is still required at line 673 (per the secondary-propagation enumeration) for forward compatibility: if Step 4a (or a future PR) raises the cascade cap, the propagation must already be in place. When that happens, the `CheckDebrisTTL` hook described above transitively covers continuation chains — `tree.Recordings.TryGetValue(child.DebrisParentRecordingId, out parentRec)` will resolve to whatever the inherited anchor is, and the three end-conditions (parent recording finalized / parent vessel destroyed / parent on-rails) compose along the chain.
11. **Re-Fly settle gap → suppress debris sampling while parent is suppressed.** During the ~1–2 s post-load Re-Fly settle window, the focused vessel's `FlightRecorder` blocks trajectory writes via private `ShouldSuppressReFlyPostLoadTrajectoryWrite(double ut, string source)`. Debris created during that window mirrors the parent: skip the per-frame Relative sample. The debris recording's first emitted Relative section starts at parent-settle-release UT, anchored to parent's live pose at that UT. Net effect: brand-new debris created during a Re-Fly load is invisible for ~1–2 s, then appears. Acceptable per the visibility-bound-to-parent contract.

   Mechanism (the predicate is private to `FlightRecorder` instance state; `BackgroundRecorder` cannot call it directly): expose a static accessor `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(string recordingId)` that reads `Patches.PhysicsFramePatch.ActiveRecorder` (the static handle to the live focus recorder; set at `FlightRecorder.cs:5463, 6382` and cleared at `6086, 6655, 8940`) and returns `true` iff the handle is non-null AND its instance state has `reFlyPostLoadSettleActive == true` AND `reFlyPostLoadSettleRecordingId == recordingId`. The accessor is read-only on the recorder's state (no mutation, no concurrency hazard). Re-Fly settle is exclusive to the focus recorder — `Patches.PhysicsFramePatch.GloopsRecorderInstance` (a separate static handle for the Gloops recorder, see `FlightRecorder.cs:175, 5461`) is intentionally NOT consulted, because Gloops sessions don't go through the Re-Fly post-load settle path. The accessor returns false when no focus recorder is active, no settle is in progress, or the queried recording isn't the settle target. For background-vessel debris (parent is itself a non-focused background recording, never the settle target), the accessor always returns false and debris sampling proceeds normally. See Step 3b §"Re-Fly settle window" for the call site.

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
2. `BackgroundRecorder.BuildBackgroundSplitBranchData` at `BackgroundRecorder.cs:1143` (pure-static factory, called from path 1's parent; the `IsDebris = !hasController` initializer is at line 1176 inside the per-child loop).
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

PR 3a touches both the recording-data schema AND the engine/recording isolation seam:

**Recording field:**

- Add `public string DebrisParentRecordingId;` at `Recording.cs:25` area, mirroring the existing `public bool IsDebris;` field.

**Interface seam** (load-bearing for PR 3c — without this, the gate cannot compile from its intended call sites):

- **Add `string DebrisParentRecordingId { get; }` to `IPlaybackTrajectory`** at `IPlaybackTrajectory.cs:70` (adjacent to the existing `bool IsDebris` property). The interface is the deliberate engine/recording isolation seam (`GhostPlaybackEngine` accesses trajectories only through it — see `IPlaybackTrajectory.cs` doc comment and CLAUDE.md §`GhostPlaybackEngine.cs`). PR 3c's gate sits in `ParsekFlight.InterpolateAndPositionRecordedRelative`, where the in-scope variable is `IPlaybackTrajectory traj`, NOT `Recording` — without this property on the interface, the gate's condition #2 (`string.IsNullOrEmpty(traj.DebrisParentRecordingId)`) does not compile.
- **Add an explicit-interface bridge on `Recording`** at `Recording.cs:928` area, mirroring the existing `bool IPlaybackTrajectory.IsDebris => IsDebris;` line:
  ```
  string IPlaybackTrajectory.DebrisParentRecordingId => DebrisParentRecordingId;
  ```
  C# does **not** let a field satisfy an interface property automatically — the field needs an explicit-interface property accessor (or the field must be promoted to an auto-property). The existing `Recording` codebase uses the explicit-interface pattern for `IsDebris`, so PR 3a follows the same pattern for symmetry.
- **Test fakes / alternative implementers** in `Source/Parsek.Tests` (e.g. any minimal `IPlaybackTrajectory` mocks for harness scenarios) need the property added as a regular auto-property — small, mechanical addition. Scenario 11 (the predicate truth-table) builds these fakes, so verify the harness builders cover the new field.
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
2. `BackgroundRecorder.RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`) — **ordering matters** (per review §S3). Today's per-child loop body at lines 1067-1130 has this order: (a) snapshot capture, (b) `tree.AddOrReplaceRecording(child)` at line 1097, (c) `tree.BackgroundMap[child.VesselPersistentId] = child.RecordingId` at line 1098, (d) build `childInitialPoint` via `CreateAbsoluteTrajectoryPointFromVessel` at line 1104, (e) `OnVesselBackgrounded(...)` at line 1108, (f) `IsDebris = true` and TTL setup at line 1115 inside the `if (!hasController)` branch. The new orchestration requires `IsDebris` and `DebrisParentRecordingId` to be set on the child Recording before step (b), since `OnVesselBackgrounded` → `InitializeLoadedState` (per the seed-point orchestration above) reads `recording.DebrisParentRecordingId` to decide whether to open the first section as `Relative` or `Absolute`. Concrete final order:

   1. Determine `hasController = newVesselInfos[i].hasController` (currently read at line 1111).
   2. Set `child.IsDebris = !hasController` immediately on the Recording (move this assignment from the `if (!hasController)` branch at line 1115 to the top of the per-child iteration).
   3. Call `ApplyDebrisAnchorContract(child, parentRecordingId)` — where `parentRecordingId` is the `parentRecordingId` parameter of `RegisterChildRecordingsFromSplit`'s caller, surfaced as a new parameter (today the function is called from `HandleBackgroundVesselSplit` and from the post-coalescer path; both have parent's recording ID in scope).
   4. Continue with snapshot capture (today's lines 1071-1095).
   5. `tree.AddOrReplaceRecording(child)` and `tree.BackgroundMap[...]` writes.
   6. Build `childInitialPoint` via `CreateAbsoluteTrajectoryPointFromVessel`.
   7. `OnVesselBackgrounded(child.VesselPersistentId, inherited, initialTrajectoryPoint: childInitialPoint)` — this now triggers the seed-point Relative transform inside `InitializeLoadedState` per Step 3b §"Initial seed point coordinate transform."
   8. TTL setup (today's `debrisTTLExpiry[child.VesselPersistentId] = branchUT + DebrisTTLSeconds` at line 1116) and the existing `Info` / `Warn` log lines.

   Net delta: hoist the `IsDebris = !hasController` line out of the `if (!hasController)` branch (it becomes the contract gate inside `ApplyDebrisAnchorContract`), add the `ApplyDebrisAnchorContract` call, surface `parentRecordingId` as a parameter on `RegisterChildRecordingsFromSplit`. The TTL setup stays gated on `!hasController`.
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

In `BackgroundRecorder.ApplyBackgroundRelativeOffset` (called from `OnBackgroundPhysicsFrame` at line 1780, periodic sampling): when `recording.DebrisParentRecordingId != null`, set `state.currentAnchorRecordingId = recording.DebrisParentRecordingId` and rely on `ApplyBackgroundCurrentAnchorToTrackSection` (`BackgroundRecorder.cs:3743`) to propagate that to `section.anchorRecordingId` — same pattern the periodic recorder already uses. Skip the candidate-list / nearest-search.

#### Structural-event seam (per investigation §Phase 1 §2)

`BackgroundRecorder.ApplyBackgroundRelativeOffset` is **also** called directly from the structural-event snapshot path at line 5460, bypassing `UpdateBackgroundAnchorDetection`. The debris-anchor enforcement must apply at both call sites: the helper that sets `section.anchorRecordingId` for debris must live inside `ApplyBackgroundRelativeOffset` (or its `*ForAnchorPose` callee), not only in the periodic anchor-detection branch. Without this, structural events for debris write Relative sections with whatever stale `state.currentAnchorRecordingId` was set in `state` — exactly the bug we're fixing.

#### Initial seed point coordinate transform

**Both creation paths** funnel into the same `OnVesselBackgrounded` → `InitializeLoadedState` flow, and the orchestration below handles both uniformly:

- **Background-vessel debris** (`BackgroundRecorder.RegisterChildRecordingsFromSplit` at `:1059`): builds the seed via `CreateAbsoluteTrajectoryPointFromVessel` at line 1104, stashes it in `pendingInitialTrajectoryPoints` and calls `OnVesselBackgrounded(...)` at line 1108.
- **Focused-vessel debris** (`ParsekFlight.CreateBreakupChildRecording` at `ParsekFlight.cs:5103`): the caller (`ParsekFlight.cs:5552-5570`) builds the seed via `BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel` at line 5561, then calls `backgroundRecorder.OnVesselBackgrounded(pid, breakupEngineState, initialTrajectoryPoint: initialPoint)` at line 5566. Same dispatch point as the background path.

Both produce a body-fixed `lat/lon/alt` seed and feed it through `OnVesselBackgrounded` → `InitializeLoadedState`, which is where `BackgroundVesselState` is constructed and the first track section opens. For debris with `DebrisParentRecordingId != null`, the seed must be transformed from body-fixed to anchor-local **before it enters the first track section**. The transform reuses `ApplyBackgroundRelativeOffsetForAnchorPose` (`BackgroundRecorder.cs:3832`), but that helper requires a fully-initialized `BackgroundVesselState` plus an `AnchorPose`, neither of which exists at `RegisterChildRecordingsFromSplit` / `CreateBreakupChildRecording` time (state is created inside `InitializeLoadedState` one frame later, after `OnVesselBackgrounded` dispatches).

Placing the orchestration inside `InitializeLoadedState` (the shared sink) covers both creation paths without duplication. Both paths get the test obligation at the bottom of this section; the propagation enumeration in §"`IsDebris` propagation surface" already lists both `RegisterChildRecordingsFromSplit` and `CreateBreakupChildRecording` as primary creation sites that must call `ApplyDebrisAnchorContract` before the seed is built — that's how `recording.DebrisParentRecordingId` becomes non-null in time for `InitializeLoadedState` to read it.

**Call order** (the orchestration the implementer needs to set up — this is where the implementation does have non-trivial work).

The state IS NOT created in `OnVesselBackgrounded` directly: that function dispatches to `InitializeLoadedState` (`BackgroundRecorder.cs:2849+`) for loaded vessels, which is where the `BackgroundVesselState` is constructed. Today, `InitializeLoadedState` opens its first track section as `ReferenceFrame.Absolute` (line 2909, `StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Absolute, ...)`) and then appends the seed via `ApplyInitialTrajectoryPoint` (line 2918) or `AppendFrameToCurrentTrackSection` (line 2920). The on-rails branch (`InitializeOnRailsState`, line 2732+) flat-appends without sectioning — debris is unlikely to enter that path on creation, but worth noting that the orchestration only applies to the loaded path.

The fix lives **inside `InitializeLoadedState`**, branching on `recording.DebrisParentRecordingId != null` before the section is opened:

1. `RegisterChildRecordingsFromSplit` continues to call `CreateAbsoluteTrajectoryPointFromVessel` and stash the body-fixed seed in `pendingInitialTrajectoryPoints` (today's behavior, unchanged).
2. `OnVesselBackgrounded` dispatches to `InitializeLoadedState` (today's behavior, unchanged).
3. **NEW** in `InitializeLoadedState`, after state is constructed but BEFORE `StartBackgroundTrackSection` opens the first section (around line 2909): branch on `treeRec?.DebrisParentRecordingId != null`. The recording-in-scope is `treeRecForSeed` (gated on `hasTreeRecording`); when `hasTreeRecording == false`, fall back to `tree.Recordings.TryGetValue(recordingId, out treeRec)`. If neither resolves, abandon the transform (open Absolute per today's path and log defensively). When the contract applies:
   a. Resolve `parentVessel = FlightRecorder.FindVesselByPid(parentRec.VesselPersistentId)` after a `tree.Recordings.TryGetValue(treeRec.DebrisParentRecordingId, out parentRec)` lookup — the live parent vessel.
   b. If `parentRec` is missing OR `parentVessel == null` (parent destroyed in the same frame), abandon the transform: open the section as `Absolute` per today's path and let the next `CheckDebrisTTL` tick end the recording. (Defensive — should be rare since the joint break producing this debris just happened, parent vessel is still around.)
   c. Build an `AnchorPose` from `parentVessel.transform.position` and `parentVessel.transform.rotation` at `seed.ut`.
   d. Set `state.currentAnchorRecordingId = treeRec.DebrisParentRecordingId`, `state.currentAnchorPid = parentVessel.persistentId`, `state.isRelativeMode = true`. (These mirror the focused recorder's `ClearCurrentRecordingAnchor` / `SetCurrentRecordingAnchor` pattern — extract a small helper `SetBackgroundCurrentAnchorForDebris(state, parentRecordingId, parentPid)` if a symmetric setter doesn't already exist.)
   e. Call `StartBackgroundTrackSection(state, initialEnv, ReferenceFrame.Relative, seed.ut)` — same call as line 2909 but with `Relative` instead of `Absolute`.
   f. Run `ApplyBackgroundRelativeOffsetForAnchorPose(state, ref seed, parentVessel, anchorPose, treeRec.DebrisParentRecordingId, AnchorCandidateSource.Live, parentVessel.persistentId, logSample: true)` to mutate the seed's `latitude/longitude/altitude` into anchor-local `(dx, dy, dz)` and `rotation` into anchor-local form. The helper also sets `section.anchorRecordingId` via `ApplyBackgroundCurrentAnchorToTrackSection`.
   g. Append the (now-Relative) seed via `ApplyInitialTrajectoryPoint(state, treeRec, seed)` — same call as today's line 2918 but the seed is already in anchor-local form.
4. Subsequent per-frame samples flow through the existing `OnBackgroundPhysicsFrame` → `ApplyBackgroundRelativeOffset` path with `state.isRelativeMode == true` already set; the per-frame anchor-write rule in §"Per-frame anchor write" handles the rest.

**Implementation note.** This is a real branch inside `InitializeLoadedState`, not a one-line patch. Estimate: ~30-40 lines of new code in `BackgroundRecorder.cs`, plus the small helper for the anchor-setter symmetry. The on-rails path (`InitializeOnRailsState`) does not need this branch — debris created from a loaded breakup never enters on-rails state on the same frame as the split, and CheckDebrisTTL will end it via Decision §10 if the debris vessel ever transitions to packed.

**Without this transform**, the body-fixed seed lands in a Relative section whose downstream interpretation expects metres-as-`(dx, dy, dz)` — the CLAUDE.md "metres-as-degrees" gotcha in reverse. The seed would then read as a wildly off-scale anchor offset.

**Test obligation** (Step 1 / harness + unit tests in PR 3b): a unit test that builds a debris recording with `DebrisParentRecordingId` set, drives the registration → `OnVesselBackgrounded` flow, and asserts that (a) the first track section has `referenceFrame == Relative` and `anchorRecordingId == parent.RecordingId`, (b) the first frame's `latitude/longitude/altitude` are anchor-local metres (small magnitudes like `O(1m)` in the just-decoupled case), not body-fixed degrees, and (c) the rotation is anchor-local (close to `Quaternion.Inverse(parentRot) * vesselRot`).

#### Parent on-rails hook (per Decision §10)

> **Review-pass amendment (post-PR-3b).** The pseudocode below shows the v9 design. **Two of its end-conditions were rewritten during PR 3b review** because their original signals were buggy. The actual shipped implementation extracts the "parent closed/superseded" check into the pure-static `DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree)` predicate. See the "Why three end-conditions and not two" subsection below for the corrected logic and the rejected signals.

Extend `CheckDebrisTTL` (`BackgroundRecorder.cs:1191-1242`). The existing loop iterates `kvp` over `debrisTTLExpiry` and uses `vesselPid = kvp.Key`, looking up `Vessel v = FlightRecorder.FindVesselByPid(vesselPid)` for the debris vessel itself. The new check needs to additionally look up the parent recording and parent vessel. **The shipped implementation:**

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
// Parent recording closed/superseded (the §"Bug evidence" closed-coverage-hole
// case): end the debris too. The DebrisParentStateGate predicate accepts the
// parent as still-active iff it is tree.ActiveRecordingId OR
// BackgroundMap[parent.VesselPersistentId] == parent.RecordingId. Anything
// that fails BOTH active-pool checks is closed/superseded.
if (DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree))
{
    if (expired == null) expired = new List<uint>();
    expired.Add(vesselPid);
    bool parentClosedAtSplit = !string.IsNullOrEmpty(parentRec.ChildBranchPointId);
    ParsekLog.Info("BgRecorder",
        $"Debris TTL: parent recording closed/superseded, ending recording: " +
        $"parentRecId={child.DebrisParentRecordingId} " +
        $"parentClosedAtSplit={parentClosedAtSplit} " +
        $"currentUT={currentUT.ToString("F2", CultureInfo.InvariantCulture)} " +
        $"childPid={vesselPid}");
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

Distinct log lines so the existing "vessel destroyed/despawned" path remains diagnosable independently. The new branches are keyed on `child.DebrisParentRecordingId != null` — legacy v11 debris (without the field) keeps its original lifetime contract. The `tree.Recordings.TryGetValue` lookup is O(1) and the parent vessel lookup mirrors the existing `FindVesselByPid` pattern used for the debris vessel itself.

**Why three end-conditions and not two.** The `DebrisParentStateGate` check is what catches the §"Bug evidence" closed-coverage-hole case (parent recording closed by a split, with the continuation owning the live vessel — the closed parent's `RecordingId` no longer matches `BackgroundMap[pid]` and isn't `ActiveRecordingId` either). The `parentVessel == null || packed || !loaded` check catches scene-transition / timewarp / vessel destruction. Without the first check, a parent whose recording is closed mid-life would let the debris keep recording Relative against a closed-and-superseded parent — exactly the failure mode evidenced by 49× `anchor-out-of-recorded-range` in the investigation bundle.

**Two rejected signals during PR 3b review.** The original v9 design used `parentRec.ExplicitEndUT < currentUT` as the "finalized" signal. That's a real bug: active background recordings update `ExplicitEndUT` only at sample boundaries, so it lags the current frame by the sample interval. Treating that lag as "finalized" would have ended every v12 debris on the next TTL tick. A first replacement used `!string.IsNullOrEmpty(parentRec.ChildBranchPointId)` as the closed signal, but `ParsekFlight.ProcessBreakupEvent:5427` sets that field on the **active focused recording** for breakup-continuous design — the recording keeps growing past the breakup. So `ChildBranchPointId`-set is a chain-topology marker, not a recording-state marker. The shipped predicate uses positive evidence of "still active" (membership in `ActiveRecordingId` or `BackgroundMap`) rather than negative evidence of "closed." `ChildBranchPointId` remains a diagnostic-only hint logged via `parentClosedAtSplit` when the predicate fires on a non-active recording.

#### Re-Fly settle window hook (per Decision §11)

**Accessor to add** (PR 3b includes this): expose a static `IsReFlyPostLoadSettleActiveForRecording(string recordingId)` on `FlightRecorder` that reads `Patches.PhysicsFramePatch.ActiveRecorder` (the focus-recorder static handle) and returns true iff that handle is non-null AND its `reFlyPostLoadSettleActive == true` AND `reFlyPostLoadSettleRecordingId == recordingId`. Returns false when no focus recorder is active, no settle is running, or the recording ID doesn't match the settle target. Does NOT consult `PhysicsFramePatch.GloopsRecorderInstance` — Gloops sessions don't enter Re-Fly settle. Implementation:

```
internal static bool IsReFlyPostLoadSettleActiveForRecording(string recordingId)
{
    var focus = Patches.PhysicsFramePatch.ActiveRecorder;
    return focus != null
        && focus.reFlyPostLoadSettleActive
        && string.Equals(focus.reFlyPostLoadSettleRecordingId, recordingId,
            StringComparison.Ordinal);
}
```

Five lines plus null guard. The two private fields stay private; the accessor is a friend that exposes a derived predicate without leaking the underlying state.

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

**Rule.** In `ParsekFlight.InterpolateAndPositionRecordedRelative` (`ParsekFlight.cs:21228+`), insert a precheck **before** each of the three calls to `TryPositionGhostRecordedRelativeAt` / `TryResolveRecordedRelativeAnchorPose`. **Insert ABOVE the resolver attempt, not after its failure** — the dominant legacy-debris failure mode is "resolver succeeds with the wrong anchor," so the existing post-failure shadow path at lines 21268/21310/21370 is unreachable for this case.

The three correct **pre-resolver** insertion points (in current main):

| Branch | Pre-resolver insertion point | Existing post-failure fallback (untouched) |
|--------|------------------------------|---------------------------------------------|
| Pre-range (`indexBefore < 0`) | **~line 21253**, before `TryPositionGhostRecordedRelativeAt(ghost, frames[0], ...)` | line 21268 (`TryUseRelativeAbsoluteShadowFallback`) |
| Single-point (`segmentDuration <= 0.0001`) | **~line 21295**, before `TryPositionGhostRecordedRelativeAt(ghost, before, ...)` | line 21310 |
| Regular interpolation | **~line 21368**, before `TryResolveRecordedRelativeAnchorPose(target, targetUT, ...)` | line 21370 |

The post-failure fallback calls (21268, 21310, 21370) **stay in place** as the resolver-failure path for non-debris and v12+ debris recordings. They are not the insertion points for the new gate.

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

#### Test seam (xUnit harness can't reach the gate as written)

The Step 1 harness is **resolver-level only** — it hashes `RelativeAnchorResolver.TryResolveRecordingPose` outputs over a UT span (per Step 1 §"Scope reduction"). The PR 3c gate sits in `ParsekFlight.InterpolateAndPositionRecordedRelative` — a `MonoBehaviour` method on the positioner, not on the resolver. Hashing resolver output won't exercise the new caller-side gate, and Scenarios 11/12 as originally written (in §3d below) cannot validate that the gate fires correctly.

The fix is to extract the gate's four-condition predicate into a pure static method that lives in a new helper file and is callable from xUnit:

```
internal static class LegacyDebrisShadowGate
{
    /// Returns true when this Relative section should bypass the resolver
    /// chain in favor of the absolute-shadow path: legacy v11 debris
    /// (DebrisParentRecordingId not yet set on load) with a populated
    /// shadow. Pure function of two interface fields and two TrackSection
    /// fields; testable from xUnit without Unity.
    internal static bool IsLegacyDebrisShadowEligible(
        IPlaybackTrajectory traj,
        TrackSection section)
    {
        if (traj == null || !traj.IsDebris) return false;
        if (!string.IsNullOrEmpty(traj.DebrisParentRecordingId)) return false;
        if (section.referenceFrame != ReferenceFrame.Relative) return false;
        if (section.absoluteFrames == null) return false;
        if (section.absoluteFrames.Count == 0) return false;
        return true;
    }
}
```

The gate code at the three call sites then reduces to a one-line check:

```
if (LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(traj, target.Section)
    && TryUseRelativeAbsoluteShadowFallback(
        recordingIndex, traj, retireSignalState, target,
        targetUT, ref cachedIndex, out interpResult))
    return;
// Fall through to the existing path (resolver-first, then shadow on resolver failure).
```

**xUnit coverage** for the predicate (Scenarios 11/12 are restated in §3d to test the predicate directly):

- Test that returns true for `IsDebris == true && DebrisParentRecordingId == null && referenceFrame == Relative && absoluteFrames.Count > 0`.
- Tests that return false for each of the four condition negations (non-debris, v12+ debris with `DebrisParentRecordingId` set, Absolute section, empty/null shadow).
- A tiny `IPlaybackTrajectory` fake suffices; no `MonoBehaviour` dependency.

**What the predicate does NOT cover**: whether `TryUseRelativeAbsoluteShadowFallback` actually produces the correct world pose from the shadow — that's an existing behavior of an unchanged method, exercised at playback by other code paths today (the post-resolver-failure branches). If a positioner-level test seam ever lands (Step 4 / §"Known follow-ups" #5), drive the full call site through it; until then, the predicate test plus the unchanged fallback behavior together establish the gate's correctness without needing to instantiate a `MonoBehaviour`.

### 3d. Re-run harness

Scenarios 4, 5, 7 hashes will change after PR 3b (the recorder change). Reset them in PR 3b with a comment: `// reset: debris parent-anchor contract introduced, see refactor-plan.md`. Scenarios 1, 2, 3, 6, 8, 9, 10 must NOT change. If any of them does, stop and investigate before merging.

For scenarios involving v12+ debris with parent unresolvable (parent destroyed, loop-rejected): the resolver returns false → no rendered position. The harness should hash this as a sentinel value (e.g. `(NaN, NaN, NaN, NaN, NaN, NaN, NaN)`) and the test expectation is that the resolver consistently produces sentinels. This verifies the "debris bound to parent visibility" property.

PR 3c adds two new test surfaces — the gate predicate is testable directly via the extracted `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` static helper (§3c §"Test seam"), and the harness gets two scenarios that exercise predicate-level coverage:

- **Scenario 11 — Predicate truth table.** Build small in-memory `IPlaybackTrajectory` fakes plus synthetic `TrackSection` instances and assert each condition independently: (a) returns true for `IsDebris && DebrisParentRecordingId == null && Relative && shadow.Count > 0`; (b) returns false for non-debris; (c) returns false for v12+ debris (`DebrisParentRecordingId` set); (d) returns false for Absolute section; (e) returns false for empty/null shadow. This is pure xUnit, runs in milliseconds, no positioner involvement.
- **Scenario 12 — `TryUseRelativeAbsoluteShadowFallback` shadow-pose correctness.** This is unchanged code today, but it has gaps in coverage; PR 3c implicitly relies on its correctness. Add a focused test that calls the existing fallback with a populated `absoluteFrames` and asserts the resolved `(worldPos, worldRot)` matches the body-fixed shadow data. Hash the output as a baseline; if a future change ever modifies the fallback, this guards it.

Together these establish that (a) the gate fires under the right conditions and only those conditions, and (b) the fallback produces the right pose. The composition — gate fires → fallback runs → correct pose rendered — is the integration concern; until a positioner-level harness exists (§"Known follow-ups" #5), in-game manual validation per §"Success criteria" §"Behavioral validation" is the integration coverage.

Both new test surfaces run only after PR 3c lands; they fail-loud if the gate isn't wired correctly.

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

1. **`SessionMerger.ComputeBoundaryDiscontinuity` Relative-frame interpretation bug.** Phase 1a now threads `RecordingFormatVersion` through `MergeTree` and measures Relative boundaries with same-anchor local deltas only for sub-millisecond seams or aligned `absoluteFrames` shadows. The remaining work is the separate Phase 1b projection-sibling cleanup plus any future policy that quarantines genuinely huge measured discontinuities; this debris plan no longer owns the merge-math fix.
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
| **Re-Fly settle gap → debris writes Relative against suppressed parent** | High without §"Re-Fly settle window" hook | Decision §11 + Step 3b §"Re-Fly settle window" hook: skip per-frame sampling while `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording(parent.RecordingId) == true` (new public accessor introduced in PR 3b — see Decision §11 for rationale). The original private `ShouldSuppressReFlyPostLoadTrajectoryWrite(double ut, string source)` instance method is not callable from `BackgroundRecorder`; the accessor is the bridge. New debris invisible for ~1–2 s post-load, then renders correctly. |
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
- **2026-05-07, v9 (this revision):** Post-Opus-review-of-v8 precision fixes. v8's review confirmed the four hard v7 errors were addressed but flagged four remaining precision gaps where the plan would mislead a literal-reader implementer:
  - **Static accessor handle named.** Decision §11 + Step 3b §"Re-Fly settle window hook" now reference `Patches.PhysicsFramePatch.ActiveRecorder` explicitly (the static handle to the focus recorder, set at `FlightRecorder.cs:5463, 6382` and cleared at `5466, 6086, 6655, 8940`). Disambiguates `PhysicsFramePatch.GloopsRecorderInstance` (the separate Gloops handle) — Re-Fly settle is exclusive to the focus recorder, so the Gloops handle is intentionally NOT consulted. Spelled out the 5-line implementation body so the implementer doesn't have to invent it.
  - **Seed-point orchestration relocated to `InitializeLoadedState`.** v8 said "immediately after state creation in `OnVesselBackgrounded`," but state creation is actually inside `InitializeLoadedState` (`BackgroundRecorder.cs:2849+`), which is what `OnVesselBackgrounded` dispatches to. v9 moves the orchestration into `InitializeLoadedState` and explicitly notes the `StartBackgroundTrackSection` call at line 2909 must flip `Absolute → Relative` for debris. Estimate updated to ~30-40 lines of new code (not the localized one-line patch v8 implied). On-rails branch (`InitializeOnRailsState`) declared out-of-scope for the orchestration since debris-on-rails is unreachable on creation.
  - **On-rails hook now also catches "parent recording finalized while still loaded."** v8's pseudocode only checked `parentVessel == null || packed || !loaded` — fine for scene transitions but missed the §"Bug evidence" closed-coverage-hole case (parent recording's `ExplicitEndUT` set while parent vessel still alive). v9 adds an explicit `!double.IsNaN(parentRec.ExplicitEndUT) && parentRec.ExplicitEndUT < currentUT` check ahead of the vessel-state check, with a distinct log line. This is what closes the gap on the 49× `anchor-out-of-recorded-range` failure mode at recording time (in addition to PR 3c's playback-time fix for legacy data). **Superseded during PR 3b review (post-v12) — the `ExplicitEndUT < currentUT` signal was rejected because active background recordings update `ExplicitEndUT` only at sample boundaries, so it lags by the sample interval and would end every v12 debris on the next TTL tick. The shipped predicate is `DebrisParentStateGate.IsParentRecordingClosedOrSuperseded` — see §"Parent on-rails hook" above for the corrected logic.**
  - **Decision §10 parent-continuation propagation reframed as forward-compat.** v8's paragraph implied the chain composition was reachable today, but with `MaxRecordingGeneration = 1` debris can't have continuations. v9 restates as: propagation line is required at `BackgroundRecorder.cs:673` for forward compatibility, but the chain composition only matters once Step 4a (or a later PR) raises the cascade cap.
  - **`RegisterChildRecordingsFromSplit` ordering spelled out** as an eight-step final order (today's order has `IsDebris = true` at line 1115 inside an `if (!hasController)` branch — the new order hoists it to the top of the per-child iteration so the seed-point orchestration in `InitializeLoadedState` can read it). Surfaces a new `parentRecordingId` parameter on the function.
  - **`IsReFlyPostLoadSettleActiveForRecording` body included verbatim** in Step 3b so the implementer doesn't have to re-derive it from the Decision §11 prose.
- **2026-05-07, v10 (this revision):** Citation/wording touch-ups from third Opus review of v9. Reviewer verdict was "ship after small fixes — citation/wording, no design changes." Five inline edits:
  - **Cleared-sites typo** in Decision §11: removed `5466` from the cleared list (line `5466` is blank in `FlightRecorder.cs`; clears are only at `6086, 6655, 8940`).
  - **`treeRec` vs `treeRecForSeed`** in Step 3b §"Initial seed point coordinate transform": clarified that the recording in scope inside `InitializeLoadedState` is `treeRecForSeed` (gated on `hasTreeRecording`), with a fallback `tree.Recordings.TryGetValue` lookup when neither resolves.
  - **Loop body line range** in Step 3b §"Primary creation sites #2": `1067-1130` (was off by one to `1067-1129`).
  - **Per-frame anchor-write wording** in Step 3b §"Per-frame anchor write": switched from "set `section.anchorRecordingId`" to "set `state.currentAnchorRecordingId` and let `ApplyBackgroundCurrentAnchorToTrackSection` propagate" — matches the existing recorder pattern.
  - **`BuildBackgroundSplitBranchData` cited line** in Step 2 §"`IsDebris` propagation surface": `BackgroundRecorder.cs:1143` for the function header (was `:1176` which is a mid-function line; the `IsDebris` initializer is at `:1176` inside the per-child loop, so both are noted).
  - No design changes; v10 is the same plan as v9 with the cited line numbers and variable names matching the actual code.
- **2026-05-07, v11 (this revision):** Fixes for review comments that surfaced after v10. Three real bugs in the plan plus three clarity gaps:
  - **PR 3c gate cannot be tested by the resolver-only harness (real bug).** Step 1's harness scope is `RelativeAnchorResolver` outputs only; PR 3c lives in `ParsekFlight.InterpolateAndPositionRecordedRelative` (the resolver's caller). Scenarios 11/12 as v10 wrote them — "hash the resolver output" — would never see the gate fire. v11 extracts the four-condition predicate into a pure-static `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible(IPlaybackTrajectory traj, TrackSection section)` helper that xUnit can call directly, and rewrites Scenario 11 as a predicate truth-table test. Scenario 12 becomes an unchanged-fallback shadow-pose test (covers `TryUseRelativeAbsoluteShadowFallback`'s correctness independently of the gate). Integration coverage (gate fires + fallback runs + correct pose) is acknowledged as a positioner-level gap addressed by §"Known follow-ups" #5 (positioner extraction) and in-game manual validation per §"Success criteria" §"Behavioral validation."
  - **Risk-table residual referenced the private `ShouldSuppressReFlyPostLoadTrajectoryWrite` (real bug).** v10's risk row "Re-Fly settle gap → debris writes Relative against suppressed parent" still listed the v7-era private call signature. v11 updates it to reference the new `FlightRecorder.IsReFlyPostLoadSettleActiveForRecording` accessor introduced in PR 3b (Decision §11) and explicitly notes the original method is not callable from `BackgroundRecorder`.
  - **PR 3a interface-seam addition was buried in a sub-bullet (clarity).** v10 mentioned adding `DebrisParentRecordingId` to `IPlaybackTrajectory` as one of several PR 3a items. v11 promotes it to its own first-class subsection, makes the load-bearing-for-PR-3c framing explicit, and notes that test fakes / alternative implementers in `Source/Parsek.Tests` need the property too. Without this addition, PR 3c's gate code does not compile from its intended call sites.
  - **Initial-seed orchestration only mentioned the BG-vessel path (clarity).** v10 said "RegisterChildRecordingsFromSplit builds the seed..." which read as if the orchestration was BG-debris-only. v11 explicitly enumerates BOTH creation paths (BG-vessel via `RegisterChildRecordingsFromSplit`, focused-vessel via `ParsekFlight.CreateBreakupChildRecording`) and notes both funnel into the same `OnVesselBackgrounded` → `InitializeLoadedState` dispatch — placing the orchestration inside `InitializeLoadedState` (the shared sink) covers both without duplication.
  - **PR 3c gate insertion-point table (clarity).** v10 had the correct line numbers in prose but mixed pre-resolver and post-resolver line numbers in adjacent sentences, which a literal-reader implementer could conflate. v11 replaces the prose with a 3-row table that explicitly pairs each branch's pre-resolver insertion point with its existing post-resolver fallback line, making it impossible to confuse the two.
  - **Audit §2c self-anchor claim corrected (real bug).** Audit said "Background recordings most often have self-anchored Relative sections (their `anchorRecordingId` points to themselves)." This is impossible: `AnchorDetector.IsRecordingAnchorEligible` rejects self-anchoring, and the background candidate builders skip the queried recording's own ID. v11 corrects the claim to: "either Absolute sections, or Relative sections cross-anchored to **another** recording."
- **2026-05-07, v12 (this revision):** Two follow-ups missed in v11.
  - **Field-as-interface-property doesn't compile (real bug).** v11's "Recording gains the property automatically by virtue of the field above" was wrong: C# fields don't satisfy interface properties. The existing `Recording` codebase resolves this for `IsDebris` via the explicit-interface pattern at `Recording.cs:928`: `bool IPlaybackTrajectory.IsDebris => IsDebris;`. v12 adds the symmetric line `string IPlaybackTrajectory.DebrisParentRecordingId => DebrisParentRecordingId;` to PR 3a's checklist and clarifies that test fakes need a regular auto-property.
  - **Audit §2f residual self-anchor wording (real bug).** v11 fixed §2c but §2f still said "Self-anchored Relative or Absolute, identical to 2c," which preserved the false invariant via cross-reference. v12 corrects §2f to "Sections are either Absolute or Relative cross-anchored to another recording, identical to 2c. Self-anchoring is not possible."
