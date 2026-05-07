# Recording & Ghost Rendering — Refactor Plan

Date: 2026-05-07
Branch: `claude/investigate-recording-policies-6tMZ9`
Companion document: `recording-and-ghost-policies-audit-2026-05-07.md`

## Goal

Fix the observed debris-rendering bugs **without introducing regressions in the working Re-Fly path.** Re-Fly recording and rendering currently work; nothing in this plan should change Re-Fly's data, code paths, or playback dispatch.

## Why "isolation by adding code paths" is the wrong instinct

The two recorders are **already physically separate**: `FlightRecorder` (focused) and `BackgroundRecorder` (everything else), mutually exclusive by `tree.BackgroundMap` membership. They share two narrow seams that are correctly factored:

- `AnchorDetector.ShouldUseRelativeFrame` (`AnchorDetector.cs:311`) — frame hysteresis decision.
- `AnchorDetector.IsRecordingAnchorEligible` (`AnchorDetector.cs:190`) — recording-level eligibility, called by both recorders.
- `FlightRecorder.IsStructuralJointBreak` (`FlightRecorder.cs:1109`) — joint-break classification, called by both joint-break handlers.

The single playback dispatch at `GhostPlaybackEngine.cs:2369` is also the right shape — one place where the Absolute/Relative contract lives, no scenario-specific branching to keep in sync.

**The actual problems the bugs come from are two different things, both addressable without splitting any working code path:**

1. **Debris policy is implicit, not contractual.** It's "nearest-eligible-anchor, like every other vessel." That's an emergent property of code that wasn't written with debris in mind — there's no contract saying "debris should be anchored to its parent." Mental models around the codebase assume there is one.
2. **No regression harness on the playback dispatch.** Re-Fly works today. Nothing prevents tomorrow's fix from silently breaking it. Splitting the dispatcher would add *more* surface to keep in sync, not less.

So the strategy is: **add tripwires (regression harness) and contract clarity (explicit debris policy). Implement the fix at the recorder. Leave the playback dispatch alone.**

---

## Step 1 — Regression harness (do this first, no behavior changes)

### What it is

A xUnit-side test fixture that builds synthetic recordings for each scenario in the audit, runs them through the playback engine offline, captures the resulting world positions over the recording's UT span, hashes the result, and asserts against a golden hash. **Every PR in the recording/playback area either preserves all hashes or explicitly resets one with a reason.**

### Why xUnit, not in-game

- Tests run in CI on every commit, not only when the developer remembers Ctrl+Shift+T in KSP.
- Hash determinism requires deterministic time-stepping and deterministic body poses — easier to control with a fake `CelestialBody`/`Planetarium` than with the real KSP runtime.
- The existing test generators (`Source/Parsek.Tests/Generators/RecordingBuilder.cs`, `VesselSnapshotBuilder.cs`, `RecordingStorageFixtures.cs`) already build synthetic recordings; we extend.
- Drawback: Unity runtime types (`Vector3`, `Quaternion`) are accessible from xUnit (Parsek.Tests already references them), but anything calling into Unity behaviour (`MonoBehaviour.Update`, coroutines) needs adapting. The playback positioning logic is mostly pure math — `TrajectoryMath`, `RelativeAnchorResolver`, and the engine's frame-resolution paths can run without Unity once `body.GetWorldSurfacePosition` is mocked.

### Scenarios to cover (initial set — extend as needed)

| # | Scenario | Why |
|---|----------|-----|
| 1 | Focused vessel, alone, Absolute throughout | Baseline — simplest possible playback |
| 2 | Focused vessel, peer at constant 1000m → Relative throughout | Verifies basic Relative path |
| 3 | Focused vessel, peer crosses 2300m / 2500m thresholds → Absolute↔Relative mid-recording | Verifies hysteresis |
| 4 | Focused vessel + first-generation debris (parent split) | The currently-broken case |
| 5 | Background vessel + first-generation debris (BG vessel split) | The other broken case |
| 6 | Re-Fly: provisional recording supersedes origin, both replayed | Verifies Re-Fly resolver chain doesn't drift |
| 7 | Re-Fly + debris created during re-fly | Cross-cutting case — most likely place for regressions |
| 8 | On-rails-only background vessel (orbit segments only, no track sections) | Verifies on-rails playback path |

### What we hash

For each scenario, sample world position at `N` evenly-spaced UTs (proposed `N = 32`) and concatenate as `(x,y,z)` doubles formatted with `R` invariant-culture, hash with SHA-256. Expand to include rotation if rotation bugs are observed.

Why position-only first: world-position is the user-visible failure mode for the debris bugs. Rotation snapshots add precision but also fragility.

### What's in / out of scope for the harness

**In:** Pure playback positioning. Frame dispatch. Anchor resolver chains. Re-Fly anchor walk-back. Loop-anchor live-PID fallback (mocked live vessel).

**Out (initial cut):** Camera follow, ghost visual mesh building, map orbit rendering, tracking station markers, scenario module save/load round-trips. These have their own test surfaces; the harness's job is the trajectory contract, not the entire ghost lifecycle.

### Estimated effort

A few days for an initial 4–5 scenarios; the remaining 3–4 are progressively cheaper as the helpers stabilize.

### Hand-off criterion

Before any code in Step 2/3 lands, the harness:
- Has at least scenarios 1, 2, 4, 6 passing on `main`'s current behavior.
- Scenario 4 (debris) is captured **as a baseline**, not as "correct" — it captures whatever the current incorrect behavior is. Step 3 will explicitly reset this hash with a justification.
- CI runs it on every PR.

---

## Step 2 — Debris contract definition (one page, before any code)

### Proposed contract

> **Debris is anchored to its parent recording for its entire lifetime.**
>
> When a split produces a child recording marked `IsDebris = true`:
> 1. The child's `Recording.AnchorRecordingId` (a new field, top-level not per-section) is set to the parent's `RecordingId` at registration time.
> 2. Every Relative `TrackSection` written into the child uses that anchor — the per-frame nearest-anchor search is **skipped** for `IsDebris` recordings.
> 3. At playback, if the parent's pose at the requested UT is unresolvable (parent destroyed, parent superseded and Re-Fly resolver returns nothing) **OR** the parent's last-known pose at that UT is more than `2500 m` from the debris's recorded relative offset, the section falls back to **Absolute** by reading the parent's last known absolute pose + the debris's offset.
> 4. The cascade cap (`MaxRecordingGeneration = 1`) stays — secondary debris (debris of debris) is still not recorded.

### Edge cases this contract addresses

| Edge case | Current behavior (bug) | Proposed behavior |
|-----------|------------------------|-------------------|
| Booster decouples, drifts away | Anchor flips to whatever is nearest, then to Absolute past 2500m → ghost can teleport when the late-life "anchor" is some other vessel that moved | Anchor stays parent for life; smooth fallback to Absolute once parent is gone or far |
| Parent re-flied (supersede chain) | Re-Fly resolver chases anchor through chain, but also through any other anchor the debris picked up during sample time → unpredictable | Anchor is always parent; resolver only chases parent supersedes |
| Debris of debris | Silently dropped (cascade cap) | Still silently dropped — same behavior, but now explicitly contracted |
| Parent destroyed mid-debris-life | Debris ghost may continue rendering with stale anchor pose | Defined fallback: Absolute from parent's last known pose |

### What *changes* at recording time

- `Recording.AnchorRecordingId` (top-level field, new): persisted in the recording metadata.
- `BackgroundRecorder.RegisterChildRecordingsFromSplit` (`BackgroundRecorder.cs:1059`): set `child.AnchorRecordingId = parentRecordingId` for `IsDebris` children.
- `BackgroundRecorder` per-frame sampling: when the recording has `AnchorRecordingId` set, write that as `section.anchorRecordingId` and skip the nearest-anchor search.

### What *doesn't* change

- The playback dispatch at `GhostPlaybackEngine.cs:2369`. Untouched.
- The `RelativeAnchorResolver` chain. Untouched (debris just feeds the same field with a different value).
- Re-Fly logic. Untouched.
- Non-debris background vessels. Untouched.

### Format version bump

The new top-level `Recording.AnchorRecordingId` requires a `RecordingFormatVersion` bump (currently 11 per CLAUDE.md). Old recordings load with `AnchorRecordingId = null` and use legacy nearest-anchor search; new debris recordings always set it. Binary `.prec` codec also bumps.

### Open questions for user (see end of plan)

---

## Step 3 — Implement the debris contract

Three change sites, in order:

### 3a. Recording schema

- Add `Recording.AnchorRecordingId` (string, default null).
- Bump `RecordingFormatVersion` (named constant, follow CLAUDE.md naming convention — e.g. `DebrisAnchorChainFormatVersion = 12 = CurrentRecordingFormatVersion`).
- Bump binary codec version to match.
- Persist in both ConfigNode and binary paths.
- Load-time default-to-null on legacy versions.

### 3b. Recorder

- `BackgroundRecorder.RegisterChildRecordingsFromSplit` sets `child.AnchorRecordingId = parentRecordingId` for debris children.
- Per-frame sampling code path that writes Relative `TrackSection.anchorRecordingId`: if `recording.AnchorRecordingId != null`, use that; skip the candidate-list / nearest-search.
- Frame decision (`AnchorDetector.ShouldUseRelativeFrame`) still runs — but the *distance* it measures is to the parent recording's pose at sample time, not to "nearest live vessel." This requires the recorder to be able to resolve the parent's pose during sampling.

### 3c. Playback (one defensive change only — not a dispatch change)

The fallback case of "parent unresolvable or > 2500m" needs to be implemented somewhere. Two options:

- **Option A (preferred):** In `RelativeAnchorResolver.TryResolveRelativeSectionPose`, if the anchor recording resolves but the resulting parent pose is > 2500m from the debris's recorded offset *or* unresolvable, fall back to Absolute reconstruction using the parent's last known absolute pose. This is local to the resolver, not the dispatch.
- **Option B:** Pre-compute fallback at recording time — write `anchorAbsoluteFallback` alongside the relative offset, like the v7 `absoluteFrames` shadow does. More storage, more deterministic.

**My preference:** Option A first; revisit Option B only if the harness shows determinism issues.

### 3d. Re-run harness

Scenario 4 hash will change. Reset it explicitly in the same PR with a comment: `// reset: debris parent-anchor contract introduced, see refactor-plan.md`. Scenarios 1, 2, 3, 5, 6, 7, 8 must NOT change. If any of them does, stop and investigate before merging.

---

## Step 4 — Zero-logic-change cleanups (optional, do last)

Honest assessment: the codebase is **already more centralized than the original audit claimed.** Real targets are slim. List, in decreasing value:

### 4a. Constant migration to ParsekConfig (pure cosmetic)

- `BackgroundRecorder.MaxRecordingGeneration = 1` → `ParsekConfig.Recording.MaxRecordingGeneration`
- `BackgroundRecorder.DebrisTTLSeconds = 60.0` → `ParsekConfig.Recording.DebrisTTLSeconds`
- Rationale: groups all tunable recording thresholds in one place alongside `RelativeFrame.EntryMeters / ExitMeters`. Pure naming change, no logic touched. Safe with the harness in place.

### 4b. Audit-correction documentation

The audit document said `AnchorEligibility` is duplicated across recorders. **It isn't** — `AnchorDetector.IsRecordingAnchorEligible` is shared. Update the audit's coupling table to remove that row, or add a footnote.

### 4c. What's NOT a refactor target

Listing these explicitly to prevent future drift:

- `BuildRecordingAnchorCandidateList` (focused) vs `BuildBackgroundRecordingAnchorCandidates` (background) — these have **different jobs**. Focused scans live peers + nearby vessels; background searches the tree's recordings. The shared eligibility check (`AnchorDetector.IsRecordingAnchorEligible`) is already the right factoring. Forcing further unification would introduce complexity, not remove it.
- `IsLiveRecordingAnchorVesselCandidate` (`FlightRecorder.cs:5048`) — focused-only by design. Background recorders don't anchor to live vessels. Don't move.
- The two `onPartJointBreak` handlers — correctly bifurcated by `BackgroundMap` membership, share `IsStructuralJointBreak`. Don't touch.

### 4d. NOT in this plan (explicit non-goals)

- Do **not** centralize the Re-Fly singleton constellation (`ActiveReFlySessionMarker` + `SpawnSuppressedByRewind` + journal phase). It's tangled but it works. Touch only if a Re-Fly bug surfaces, and only with its own focused harness scenario.
- Do **not** change the cascade cap from 1.
- Do **not** unify the focused-debris vs background-debris registration paths (crash coalescer vs direct deferred check). They have different timing semantics for legitimate reasons (#362, #263).

---

## Risk analysis

| Risk | Likelihood | Mitigation |
|------|-----------|-----------|
| Step 1 harness fails to capture a regression that bites in production | Medium | Start with the most-likely-to-regress scenarios (debris, Re-Fly, Re-Fly+debris). Expand harness as bugs are found in the field. |
| Step 3 recorder change accidentally affects non-debris background vessels | Medium-low | Gate everything on `recording.AnchorRecordingId != null`, which is set only for `IsDebris` children. Harness scenario 5 (BG vessel non-debris) catches drift. |
| Step 3c fallback logic introduces a new playback path that breaks Re-Fly | **High if not careful** | Strictly local to `RelativeAnchorResolver`. Must be invoked only when a debris recording's parent is unresolvable. Re-Fly resolver runs *before* this fallback — if Re-Fly resolves the anchor, we never enter the fallback. Test: Re-Fly+debris scenario 7. |
| Format version bump corrupts legacy saves | Low | Standard CLAUDE.md pattern: load-time default-on-legacy, write only on new recordings. Existing tests for format-version migration cover the round-trip. |
| Harness becomes flaky due to mocked Unity types | Medium | Keep mocks minimal: deterministic Planetarium time, fake CelestialBody with closed-form `GetWorldSurfacePosition`, no Unity coroutines. Tests run synchronously. |

---

## Decisions (confirmed by user 2026-05-07)

1. **Harness location:** `Source/Parsek.Tests` (xUnit). Deterministic mocks for `CelestialBody`/`Planetarium`. CI on every PR.
2. **Debris fallback when parent unresolvable / > 2500m:** parent's last known pose + debris's recorded offset. No absolute-shadow at sample time. Revisit only if the harness shows determinism issues.
3. **Cascade cap:** stays at `MaxRecordingGeneration = 1`. Secondary debris remains untracked.
4. **Step 4 cleanups:** defer to a separate PR after the debris fix lands. Keeps the debris-fix PR diff focused.

---

## Proposed PR sequence

1. **PR 1**: harness skeleton + scenarios 1, 2, 6 (no behavior changes; baseline goldens for non-debris paths). Lowest risk.
2. **PR 2**: extend harness to cover scenarios 4, 5, 7 (capture *current* debris behavior, including the bugs). Lowest risk.
3. **PR 3**: debris contract — recorder change + format version bump + scenario-4/5/7 hash resets with justification. Highest risk, but harness from PRs 1-2 catches Re-Fly regressions.
4. **PR 4** (optional): Step 4 cleanups.

Each PR independently shippable; bisect-friendly if anything regresses.

---

## Success criteria

- Observed debris bugs no longer reproduce on the user's known-broken cases.
- All Re-Fly tests (existing in-game + harness scenarios 6, 7) still pass.
- Scenario hashes for non-debris non-Re-Fly cases (1, 2, 3, 5 non-debris, 8) are unchanged across PR 3.
- No new tangled coupling introduced (Step 4d list is preserved).
