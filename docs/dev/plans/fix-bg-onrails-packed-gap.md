# Plan: Preserve Background On-Rails Packed Coasts as Checkpoint Sections

Branch: `fix-bg-onrails-gap`

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-bg-onrails-gap`

Status: implemented in the `fix-bg-onrails-gap` worktree; retained as design and validation notes.

## Problem Statement

The `2026-05-10_1713` log contains two repeated merger warnings for `Kerbal X Probe`:

- `KSP.log:17718`, log time `17:04:28`, recording `32d9674c9bad4b5091c78aaa761eb11e`
- `KSP.log:32885`, log time `17:05:49`, recording `cbec2f47904f450e9b92552c6c0ff8f1`

Both warnings report the same physical gap:

```text
[Parsek][WARN][Merger] MergeTree: boundary discontinuity=845085.30m
ut=958.87 vessel='Kerbal X Probe'
prevRef=Absolute nextRef=Absolute prevSrc=Background nextSrc=Background
dt=481.66s expectedFromVel=1002169.02m ratio=0.84 cause=unrecorded-gap
```

The first warning fires at `section[2]`; the second fires at `section[1]`. The lower later index is expected after a re-merge or split collapses earlier sections, but the same boundary remains.

This is not an SOI transition and not a relative-frame decode issue. The two adjacent sections are absolute, background-recorded coasts around a packed/on-rails interval.

## Evidence Summary

From `logs\2026-05-10_1713\parsek\Recordings\32d9674c9bad4b5091c78aaa761eb11e.prec.txt`:

- Track section 0: Absolute Background, about `456.55 -> 466.23`
- Track section 1: Absolute Background, about `466.23 -> 479.26`
- Track section 2: Absolute Background, about `909.02 -> 960.51`
- Top-level orbit segments:
  - `479.25749137883366 -> 578.22850700383356`
  - `578.22850700383356 -> 909.023508884545`
  - `909.023508884545 -> 960.51459653102938`

From `logs\2026-05-10_1713\parsek\Recordings\cbec2f47904f450e9b92552c6c0ff8f1.prec.txt`:

- Track section 0: Absolute Background, `466.23452445989165 -> 477.33452445988155`
- Track section 1: Absolute Background, `958.87146746423491 -> 979.7714674642159`
- No top-level orbit segments remain.
- Section 1 carries `bdisc=845085.3`.

From the log around the transition:

- `17:03:58.581`: Background recorder closes loaded section and flushes two track sections.
- `17:03:58.581`: KSP packs `Kerbal X Probe` for orbit.
- `17:04:05.775`: Background recorder closes an on-rails orbit segment covering roughly `907.4 -> 958.9`.
- `17:04:05.775`: Mode transition `on-rails -> loaded`, then a loaded section starts at `UT=958.87`.
- `PatchedConicSnapshot` reports solver unavailable in the same window, but this is not the root cause. The packed interval is already represented as `OrbitSegment` payload in the earlier sidecar.

The later recording proves that a section-authoritative rewrite or split can discard the top-level orbit-segment bridge, leaving only two Absolute Background track sections and the same 845 km discontinuity.

## Current Classifier Behavior

`SessionMerger.ClassifyBoundaryDiscontinuity` can emit:

- `no-prev`
- `prev-no-frames`
- `next-no-frames`
- `invalid-data`
- `frame-mismatch`
- `unrecorded-gap`
- `save-load-teleport`
- `sample-skip`

The observed boundary is classified as `unrecorded-gap` because the discontinuity is physically plausible for the elapsed time:

- `disc=845085.30m`
- `dt=481.66s`
- `expectedFromVel=1002169.02m`
- `ratio=0.84`

The classifier is doing what it was designed to do. The problem is that the section tree no longer contains the on-rails bridge that explains the gap.

## Root Cause

The background recorder currently represents a packed/on-rails coast as top-level `Recording.OrbitSegments`, not as `TrackSection` entries.

That becomes fragile because:

1. `SessionMerger` diagnostics and overlap logic are section-based. Top-level `OrbitSegments` do not prevent an Absolute Background section from being adjacent to another Absolute Background section.
2. Section-authoritative persistence and optimizer split paths can keep `TrackSections` while dropping or partitioning away top-level `OrbitSegments`.
3. Once top-level orbit segments are gone, the recording shape becomes `Absolute Background -> Absolute Background` across a real 481 second packed interval. The merger correctly reports a large discontinuity.

So the 845 km gap is real in the sampled-frame stream. It is not evidence that endpoint frames were stripped or decoded in the wrong reference frame. It is a recording-pipeline representation bug: a real on-rails interval was stored outside the section model that later code treats as authoritative.

Important existing context: `BackgroundRecorder.StartCheckpointTrackSection` already creates the desired checkpoint-section shape for a loaded background vessel transitioning to on-rails:

```text
environment=SegmentEnvironment.ExoBallistic
referenceFrame=ReferenceFrame.OrbitalCheckpoint
source=TrackSectionSource.Checkpoint
frames=[]
checkpoints=[]
minAltitude=float.NaN
maxAltitude=float.NaN
```

The missing path is not loaded-to-on-rails section creation. The failing `Kerbal X Probe` path is an on-rails close/checkpoint path: packed orbit segments close during warp checkpoints, SOI handling, off-rails transitions, and finalization, but those closed segments are not preserved as checkpoint `TrackSection` payloads.

Also note that `SessionMerger.HealBackgroundActiveUnrecordedGapBoundaries` already heals one different seam class by inserting an interpolated point, but only for `Background -> Active` boundaries with comparable non-checkpoint reference frames. The observed case is `Background -> Background`, so this plan does not conflict with that healer.

## Recommendation

Fix the recording representation first. Do not smooth, hide, or mark this warning as acknowledged until packed/on-rails coasts are preserved inside the section tree.

The intended post-fix section shape is:

```text
Absolute Background -> OrbitalCheckpoint Checkpoint -> Absolute Background
```

With that shape, `SessionMerger.ComputeBoundaryDiscontinuity` does not try to measure a direct Cartesian boundary across the packed interval because the adjacent reference frames differ. The orbital checkpoint section becomes the explicit bridge.

Canonical representation decision: durable packed/on-rails orbital payload should live in checkpoint `TrackSection`s. Flat `Recording.OrbitSegments` should be treated as a derived runtime cache rebuilt from sections for consumers such as playback, ghost-map presence, spawning, diagnostics, and existing `IPlaybackTrajectory` readers. If implementation temporarily appends to flat `OrbitSegments` for in-memory continuity, that append must be tied to the same helper and validated against section rebuilds; section-authoritative sidecars should not depend on separately persisted duplicate orbit data.

## Goals

- New background on-rails orbit intervals are persisted as `TrackSection` entries with:
  - `referenceFrame = ReferenceFrame.OrbitalCheckpoint`
  - `source = TrackSectionSource.Checkpoint`
  - `environment = SegmentEnvironment.ExoBallistic`
  - `checkpoints = [closed OrbitSegment]`
  - no Cartesian `frames`
- Existing top-level non-predicted `OrbitSegments` can be normalized into checkpoint track sections before merge/split code relies on section adjacency.
- The warning remains meaningful for true missing physics samples that have no checkpoint or orbit bridge.
- Section-authoritative sidecar saves preserve the packed interval.
- The fix does not reinterpret v6 Relative-frame payloads and does not affect active recorder relative-frame contracts.

## Non-Goals

- Do not synthesize 481 seconds of fake Cartesian samples.
- Do not linearly smooth the 845 km jump.
- Do not suppress all `unrecorded-gap` warnings.
- Do not convert predicted terminal-orbit tails into ordinary track sections unless a separate design explicitly calls for that.
- Do not require closing, restarting, or killing KSP during validation.

## Design

### 0. Localize the Existing Orbit-Segment Drop

Before changing behavior, pin the code path that converted the earlier recording with useful top-level `OrbitSegments` into the later section-authoritative recording with only two Absolute Background sections.

Likely suspects:

- `RecordingOptimizer.SplitAtSection`, which partitions top-level `OrbitSegments` separately from `TrackSections`, then calls `TrySyncFlatTrajectoryFromTrackSections` / `TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail`.
- `TrajectorySidecarBinary` and `TrajectoryTextSidecarCodec` section-authoritative writes, which intentionally omit flat orbit lists and rely on rebuilding `rec.OrbitSegments` from checkpoint sections on load.
- `RecordingStore.TrySyncFlatTrajectoryFromTrackSections`, which can rebuild flat orbit payloads to empty when the section model contains only Absolute sections.

This localization is not optional. The fix is not complete unless the code path that dropped the bridge is either repaired or explicitly bypassed by earlier normalization.

### 1. Add a Single Checkpoint-Section Builder

Add a helper that converts one closed on-rails `OrbitSegment` into a checkpoint `TrackSection`.

Candidate location:

- `BackgroundRecorder`, if used only for new background recording.
- A small shared helper near `TrackSection` or `TrajectoryMath`, if also used by merger, load, or optimizer normalization.

Preferred shape:

```csharp
internal static TrackSection BuildOnRailsCheckpointSection(OrbitSegment segment)
```

Output contract:

- `startUT = segment.startUT`
- `endUT = segment.endUT`
- `environment = SegmentEnvironment.ExoBallistic`
- `referenceFrame = ReferenceFrame.OrbitalCheckpoint`
- `source = TrackSectionSource.Checkpoint`
- `checkpoints = new List<OrbitSegment> { segment }`
- `frames = new List<TrajectoryPoint>()`
- `minAltitude = float.NaN`
- `maxAltitude = float.NaN`
- `isBoundarySeam = false`

Use the canonicalized orbit segment already produced by `CreateOrbitSegmentFromVessel` and closed by `CloseOrbitSegment`.

Refactor `StartCheckpointTrackSection` to use the same field initialization or mirror it exactly. The loaded-to-on-rails placeholder currently opens a checkpoint section with an empty checkpoint list; the on-rails close path should produce the completed form with the closed `OrbitSegment` stored in `checkpoints`.

### 2. Emit Checkpoint Sections When Background On-Rails Segments Close

In `BackgroundRecorder.CloseOrbitSegment(BackgroundOnRailsState state, double ut)`:

1. Set `state.currentOrbitSegment.endUT = ut`.
2. Build a checkpoint `TrackSection` from the closed segment.
3. Append it to `treeRec.TrackSections`.
4. Rebuild or update flat `treeRec.OrbitSegments` from section payload so runtime consumers still see orbit data.
5. Mark files dirty and update `ExplicitEndUT`.
6. Log an aggregate or one-shot message that includes `recordingId`, `pid`, `startUT`, `endUT`, `body`, and whether a checkpoint section was emitted.

This makes the section model authoritative while preserving the in-memory flat orbit cache used by current playback and diagnostics paths.

### 3. Guard Against Duplicate Close/Emit Paths

`CloseOrbitSegment` can be reached from:

- background vessel going off rails,
- checkpoint-all-vessels,
- SOI changes,
- scene shutdown/finalization paths, depending on state.

Add a local duplicate guard before appending a checkpoint section. Scope the guard to the most recently appended checkpoint section for that recording, not a whole-recording historical scan. The failure mode is repeated close/finalization entry for the current open segment, not arbitrary historical duplicate segments.

Compare:

- same `ReferenceFrame.OrbitalCheckpoint`
- same `TrackSectionSource.Checkpoint`
- same start/end UT within a tight epsilon
- same body/reference identity if available

The guard should skip only exact already-emitted segments. It should not merge adjacent SOI segments or collapse different conics.

### 4. Preserve Existing SOI Behavior

For `OnBackgroundVesselSOIChanged`, each closed orbit segment should become its own checkpoint section. This yields:

```text
Absolute BG -> Checkpoint Kerbin -> Checkpoint Mun -> Absolute BG
```

or similar. Adjacent checkpoint sections are acceptable because their orbital payloads are explicit, and direct Cartesian boundary discontinuity measurement is not applicable.

### 5. Add Legacy Normalization Before Merge/Split Reliance

The source-recording fix prevents new bad data, but existing recordings like `32d9674c9bad4b5091c78aaa761eb11e` already contain useful top-level orbit segments without checkpoint sections.

Add a targeted normalization pass before any path can make section-authoritative decisions:

```text
EnsureCheckpointSectionsForTopLevelOrbitSegments(recording)
```

Rules:

- Inspect non-predicted top-level `OrbitSegments`.
- Skip segments that already have an overlapping checkpoint `TrackSection`.
- Skip zero-duration or invalid segments.
- Append checkpoint sections for real on-rails intervals that do not overlap physical `TrackSections`.
- Prefer conservative gating: if uncertain whether a segment is a terminal prediction, skip and log the skip reason.

This helper should be shared by, or invoked from, all of the following:

- load/repair or store-normalization code, so old recordings are repaired before users split/save them;
- `SessionMerger`, before overlap resolution;
- optimizer split paths, before `SplitAtSection` partitions sections and top-level orbit payload separately.

This protects old sidecars and the exact replay path that produced the first warning. SessionMerger-only normalization is not enough because `RecordingOptimizer.SplitAtSection` can otherwise split a legacy recording and then rebuild flat orbit payload from Absolute-only sections, reproducing the second warning shape.

Do not inject predicted terminal tails by default. The same vessel has stale terminal-orbit healing warnings in this log, and terminal orbit repair should remain separate from packed-coast section bridging.

### 6. Revisit Section-Authoritative Save/Load

Verify that a sidecar containing:

```text
TRACK_SECTION ref=OrbitalCheckpoint source=Checkpoint checkpoints=1
```

round-trips through:

- `TrajectorySidecarBinary`
- `TrajectoryTextSidecarCodec`
- `RecordingStore`
- any binary/ConfigNode sidecar paths
- `TrySyncFlatTrajectoryFromTrackSections`

The expected durable result is that checkpoint sections persist. Flat `rec.OrbitSegments` may be absent from section-authoritative sidecar payloads, but must be rebuilt after load from checkpoint sections. A later split or re-save must not produce a recording with only the two Absolute Background sections.

### 7. Update Optimizer Assumptions

`RecordingOptimizer` currently documents that on-rails background vessels emit `OrbitSegments` but not env-classified `TrackSections`. That will become stale.

Update that comment and verify optimizer behavior. The new invariant wording should be:

```text
on-rails emits only OrbitalCheckpoint-frame sections; per-orbit Atmospheric/ExoBallistic toggles still cannot occur because env classification remains gated behind packed/on-rails early returns.
```

Verify:

- A checkpoint section should not force a bogus split solely because it bridges packed orbit.
- Physical sections should still split on meaningful environment/reference/source boundaries.
- If a split cuts near a checkpoint section, the section and its checkpoint payload must be retained on the correct side or safely duplicated only when the interval genuinely overlaps the split.
- Many adjacent same-body `OrbitalCheckpoint`/`ExoBallistic` sections produce zero split candidates.
- Adjacent checkpoint sections across different bodies deliberately remain unsplit under the current optimizer rule that keeps same-class ExoBallistic transfer coasts cohesive; pin that behavior in the test name.

### 8. Keep the Merger Warning Unsuppressed Initially

Do not add a "warned once" marker as the primary fix.

After the checkpoint bridge exists:

- `Absolute -> Checkpoint` boundaries are not Cartesian-continuity comparable.
- `Checkpoint -> Absolute` boundaries are not Cartesian-continuity comparable.
- If the same `unrecorded-gap` warning still appears, it means the recording still has an actual missing-sample gap with no orbital bridge.

Only consider persisted warning de-duplication later, and only for diagnostics noise, not data correctness.

## Files Likely To Change

- `Source/Parsek/BackgroundRecorder.cs`
  - emit checkpoint track sections on closed background on-rails orbit segments
  - add duplicate guard and logging
  - refactor or mirror `StartCheckpointTrackSection` so the loaded-to-on-rails placeholder and completed on-rails close section use one field contract
- `Source/Parsek/SessionMerger.cs`
  - normalize legacy top-level orbit segments into checkpoint sections before overlap resolution
  - add focused diagnostic logging for injected/skipped checkpoint sections
- `Source/Parsek/RecordingOptimizer.cs`
  - update stale comment and harden split handling if tests reveal an issue
- `Source/Parsek/RecordingStore.cs`
  - likely home for shared load/repair normalization so legacy top-level orbit bridges are converted before optimizer split/save paths
  - verify section-authoritative sync rebuilds flat `OrbitSegments` from checkpoint sections after load
- `Source/Parsek/TrajectorySidecarBinary.cs`
  - verify section-authoritative binary writes persist checkpoint sections and rebuild flat orbit cache on read
- `Source/Parsek/TrajectoryTextSidecarCodec.cs`
  - verify text sidecar parity and section-authoritative rebuild behavior
- `Source/Parsek.Tests/EccentricOrbitOptimizerInvariantTests.cs`
  - update invariant wording and add adjacent checkpoint-section optimizer cases
- `Source/Parsek.Tests/Harness/HarnessScenarios.cs`
  - update comments and any assumptions that packed on-rails means no sections at all
- `.claude/CLAUDE.md`
  - update on-rails invariant wording
- `AGENTS.md`
  - update on-rails invariant wording in the canonical agent instructions
- `docs/dev/research/recording-and-ghost-policies-audit-2026-05-07.md`
  - update the audit wording from "only OrbitSegments, no TrackSections" to the narrowed invariant
- `docs/dev/research/extending-rewind-to-stable-leaves.md`
  - update §S16 if it encodes the old no-section invariant
- `docs/dev/research/optimizer-meaningful-split-rule.md`
  - update the background on-rails section to allow orbit-only checkpoint sections
- `Source/Parsek.Tests/...`
  - add focused regression coverage
- `docs/dev/todo-and-known-bugs.md`
  - add or update a todo entry for this bug before committing behavior changes
- `CHANGELOG.md`
  - add the fix under the current version once implementation begins

## Test Plan

### Unit Tests

Add tests for the checkpoint builder:

- converts `OrbitSegment` start/end/body/orbit parameters into one `TrackSection`
- uses `ReferenceFrame.OrbitalCheckpoint`
- uses `TrackSectionSource.Checkpoint`
- leaves `frames` empty
- stores exactly one checkpoint

Add tests for `BackgroundRecorder.CloseOrbitSegment`:

- orbiting background on-rails close appends a checkpoint `TrackSection` and refreshes/rebuilds flat `OrbitSegments`
- duplicate close/finalization entry does not append duplicate checkpoint sections when the most recent checkpoint section already matches the current closed segment
- atmospheric/landed no-payload cases still do not emit false checkpoint sections
- SOI close emits one checkpoint per closed conic

Add tests for merger normalization:

- input: Absolute BG section, top-level non-predicted orbit segment, Absolute BG section
- expected merged sections: Absolute BG, OrbitalCheckpoint Checkpoint, Absolute BG
- no `MergeTree: boundary discontinuity=... cause=unrecorded-gap` warning is emitted for the bridged interval
- existing checkpoint section prevents duplicate injection
- predicted orbit segment is skipped
- invalid or zero-duration orbit segment is skipped
- exact legacy shape from the investigation: multiple top-level non-predicted orbit segments, including one that overlaps the following Absolute Background section, normalizes into checkpoint sections and lets `ResolveOverlaps` trim lower-priority checkpoint overlap without recreating an Absolute-to-Absolute boundary

Add persistence tests:

- binary section-authoritative round-trip preserves checkpoint `TrackSection.checkpoints`
- text sidecar round-trip preserves checkpoint `TrackSection.checkpoints`
- section-authoritative rewrite may omit flat top-level `OrbitSegments`, but load rebuilds `rec.OrbitSegments` from checkpoint sections
- split/re-merge of the post-fix shape keeps a checkpoint bridge between the two Absolute Background sections
- legacy recording with top-level orbit bridge but no checkpoint sections is normalized before save/split so the bridge is not lost

Add optimizer tests:

- checkpoint bridge does not create an unwanted optimizer split by itself
- splitting around a checkpoint bridge does not strip the checkpoint payload from both resulting recordings
- `N_OnRails_Closes_Same_Body_Same_Env_Produce_Zero_Split_Candidates`: adjacent `OrbitalCheckpoint` / `ExoBallistic` / same-body sections are treated as not a splittable boundary
- `OnRails_Checkpoint_Body_Change_StaysCohesive`: adjacent checkpoint sections with different bodies pin the current same-class ExoBallistic transfer-coast behavior

Add negative regression test:

- two Absolute Background sections with no top-level orbit segment and no checkpoint still produce an `unrecorded-gap` diagnostic

### Headless Validation

Run from the worktree root:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
```

If local testhost or NuGet environment issues block execution, follow repo guidance:

```powershell
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
```

Record the blocker explicitly if that fallback is needed.

### Runtime Validation

Use an in-game reproduction of the `Kerbal X Probe` packed-on-rails path.

Expected log/result:

- no repeat of `boundary discontinuity=845085.30m` for the packed interval
- new sidecar has a checkpoint `TRACK_SECTION` spanning the packed/on-rails UT interval
- loaded runtime recording rebuilds `rec.OrbitSegments` from checkpoint sections so current playback/diagnostic consumers still see orbit data
- re-merge/split of the recording does not collapse back to two adjacent Absolute Background sections

Per repo safety rules, do not close, kill, restart, or otherwise manipulate the KSP process from the agent session. If the game locks artifacts or needs a restart, ask the user to do it manually.

## Acceptance Criteria

- Fresh background packed/on-rails coasts are represented inside `TrackSections`.
- Legacy top-level non-predicted orbit segments are normalized into checkpoint sections before merge, split, and section-authoritative save paths can drop the bridge.
- The observed `Kerbal X Probe` shape no longer produces a direct Absolute Background to Absolute Background discontinuity across the packed interval.
- True missing-sample gaps still warn.
- Sidecar persistence preserves the bridge after split/re-merge.
- Comments and docs no longer claim background on-rails emits no track sections at all; they instead preserve the narrower invariant that on-rails emits no env-classified per-frame sections.

## Risks

- During implementation, code may briefly carry orbit data in both checkpoint `TrackSections` and flat `OrbitSegments`. Mitigation: make checkpoint sections the durable source of truth and treat flat `OrbitSegments` as a rebuilt cache; add binary/text section-authoritative round-trip tests that prove the cache rebuilds from sections.
- Predicted terminal tails could be accidentally promoted to real checkpoint sections. Mitigation: skip predicted segments in legacy normalization.
- Checkpoint sections could interfere with optimizer split predicates. Mitigation: add explicit optimizer tests and update stale assumptions.
- Very short on-rails intervals could add noisy checkpoint sections. Mitigation: ignore invalid or zero-duration segments and consider a small duration threshold only if tests reveal churn.
- SOI transitions may create adjacent checkpoint sections. This is acceptable, but tests should cover the shape.

## Open Questions

- Should legacy normalization live in `RecordingStore` load/repair as the primary path, with `SessionMerger` and optimizer calls as defensive backstops, or should it be a shared helper invoked explicitly by each caller?
- Which current flat-orbit consumers need a rebuilt `rec.OrbitSegments` cache immediately after `CloseOrbitSegment`, and which can tolerate rebuild-on-load/save only?
- Does playback already render `OrbitalCheckpoint` sections for background checkpoint source exactly like active checkpoint source, or does it need a small source-policy adjustment?
- Are there any UI/table formatters that assume checkpoint sections are active-recorder-only?

## Todo-Doc Coverage

Existing coverage is partial:

- A closed todo entry covers older `MergeTree` boundary warnings for Background to Active sections.
- A broader open todo mentions large `MergeTree`/`unrecorded-gap` discontinuities and quarantine/split behavior.
- There is no dedicated entry for Background packed/on-rails coast stored only as top-level `OrbitSegments` and later lost during section-authoritative split/re-save.

When implementing, add a dedicated `todo-and-known-bugs.md` entry or extend the broader open entry with this exact failure mode and link the fix approach to checkpoint track sections.

## Commit Cadence

Per project documentation rules, do not land this plan as a standalone planning commit. Commit it alongside the behavior changes so the docs match the code in the same commit.
