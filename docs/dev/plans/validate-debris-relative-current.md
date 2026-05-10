# Validate Debris Relative Playback on Current Main

## Scope

Worker D validation pass for the retained debris relative playback reports around stale tails and sparse Relative sections. This plan intentionally does not propose production or test edits unless current-main evidence confirms an open bug.

## Current-main status

- Worktree base: `origin/main` at `c84010d8` (`Merge pull request #818 from vl3c/fix-tail-orbit-state-vector-frame`).
- PR #817 is already included: `02abca79` (`Merge pull request #817 from vl3c/fix/debris-recorder-tail-invariant`, merged `2026-05-10 22:40:08 +0300`).
- Current recording format is v12 (`RecordingStore.DebrisParentRecordingFormatVersion` / `CurrentRecordingFormatVersion`).
- Current main has `DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording`, called from recorder/finalizer paths to clamp/drop parent-anchored debris Relative sections to recorder-persistable authored coverage and trim flat stale-tail points. Reviewer evidence pins the relevant production paths to `BackgroundRecorder.cs:1486`, `BackgroundRecorder.cs:6010`, and `RecordingFinalizationCacheApplier.cs:119`.
- `RecordingSidecarStore.LoadRecordingFiles` does not call the normalizer. Loading retained pre-#817 sidecars can replay stale shape; that is not evidence that current-main recorder/finalizer paths generated or rewrote stale data.
- Current main has playback-side guards in `DebrisRelativePlaybackPolicy` and `GhostPlaybackEngine`:
  - parent-anchored debris retires outside authored Relative/shadow coverage;
  - covered v12 parent-anchored debris routes through the section `absoluteFrames` shadow before legacy parent-relative reconstruction;
  - single absolute shadow points do not count as interpolation coverage.

## Retained-log status

- No retained log bundle under `C:\Users\vlad3\Documents\Code\Parsek\logs` postdates PR #817. The newest bundle is `2026-05-10_2123`.
- `logs/2026-05-10_2123/git-state.txt` shows commit `4c8bfde6` (`Merge pull request #814 from vl3c/fix-bg-onrails-gap`) and collection time `2026-05-10 21:23`, before PR #817 merged at `22:40 +0300`.
- `logs/2026-05-10_1713/git-state.txt` shows commit `0da7110e` (`Merge pull request #787 from vl3c/fix-debris-finalizer-sampling`) with local modifications, also before PR #817.
- The retained bundles therefore confirm the old stale-tail/sparse-section shape, but they do not prove a current-main failure.
- Targeted grep checks across `2026-05-10_2123` and `2026-05-10_1713` found zero matches for:
  - `parentDriftFromRecorded`
  - `frameContract=frozen-refly`
  - `mutableActiveReFlyAnchor=true`
- `2026-05-10_2123` does include `anchor-rotation-shadow-route` and `recorded-relative-retired` lines, which shows pre-#817 playback was already attempting shadow routing/retirement, but the recording-side stale tail was still present in that build.

## Conclusion

No current-main production bug is confirmed from retained evidence. The retained logs predate the recorder normalization that PR #817 added, and current main has explicit recorder-side and playback-side defenses for the reported failure mode.

Recommended next step is validation/monitoring, not a speculative code fix.

## Validation plan

1. Headless focused tests on current main:
   - `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --no-restore --filter "FullyQualifiedName~DebrisRelativeRecorderPolicyTests|FullyQualifiedName~DebrisRelativeCoveragePrimitivesTests|FullyQualifiedName~GhostPlaybackEngineTests|FullyQualifiedName~BackgroundTrackSectionTests|FullyQualifiedName~RecordingFinalizationCacheTests"`
   - Expected: pass. This slice covers stale-tail clamping, sparse coverage rules, parent-anchored debris retirement, absolute-shadow coverage, and finalization-cache application.

2. Fresh current-main recording lane:
   - Build from this/current-main worktree with `dotnet build Source/Parsek/Parsek.csproj`.
   - Start from a clean or clearly identified save state and create a new post-#817 Kerbal X debris separation recording.
   - Inspect only sidecars produced or rewritten by this run. Do not use retained pre-#817 sidecars to judge current-main recorder correctness.
   - Collect evidence with `python scripts/collect-logs.py debris-relative-current-main-fresh-recording`.
   - Required evidence:
     - `git-state.txt` shows `c84010d8` or newer and includes PR #817 in ancestry.
     - New/re-written v12 debris recordings with `DebrisParentRecordingId` have Relative section `endUT` no later than recorder-persistable authored coverage from `frames`, two-point `absoluteFrames`, or non-predicted checkpoints, unless a later Absolute boundary section legitimately supplies the endpoint.
     - Flat `Recording.Points` do not extend beyond the latest renderable authored tail unless a later Absolute boundary section legitimately supplies the endpoint.
     - `ParentAnchoredDebrisTailNormalize` appears only when current-main mutates stale data during a recorder/finalizer write path; when it appears, the associated sidecar is dirty/saved and endpoint state is refreshed.

3. Legacy replay lane:
   - Replay retained pre-#817 data, including the known stale `dc538...` shape, only to validate current-main playback behavior.
   - Do not expect load-time recorder normalization, because `RecordingSidecarStore.LoadRecordingFiles` does not call the normalizer.
   - Collect evidence with `python scripts/collect-logs.py debris-relative-current-main-legacy-replay`.
   - Required evidence:
     - `git-state.txt` shows `c84010d8` or newer and includes PR #817 in ancestry.
     - Covered debris emits `anchor-rotation-shadow-route` with `mode=always` while `absoluteFrames` cover playback UT.
     - Stale-tail playback emits `recorded-relative-retired` with `coverageReason=relative-and-shadow-frames-out-of-range` or another explicit coverage reason when the playback UT exits authored Relative/shadow coverage.
     - The stale pre-#817 sidecar is not classified as a current-main recorder failure merely because replay loaded it unchanged.
     - There is no `parentDriftFromRecorded`, `frameContract=frozen-refly`, or `mutableActiveReFlyAnchor=true` evidence unless new diagnostics intentionally reintroduce those names.

4. In-game diagnostics:
   - Run relevant in-game diagnostics via `Ctrl+Shift+T`, especially parent-anchored debris coverage / ghost playback tests.
   - Keep the fresh recording lane and legacy replay lane evidence separate in retained logs and notes.

## Fix criteria if validation fails

Only open a production fix if a post-#817/current-main bundle proves one of these:

- A parent-anchored v12 debris Relative section persists with `endUT` beyond both Relative frames and `absoluteFrames`, no later Absolute/checkpoint coverage justifies the endpoint, and the recording was generated or rewritten by current-main recorder/finalizer paths.
- `ParentAnchoredDebrisTailNormalize` ran on current-main stale data but failed to mark the recording dirty, refresh endpoint state, or save the corrected sidecar.
- Playback renders parent-anchored debris outside authored coverage without a `recorded-relative-retired` guard.
- Covered parent-anchored debris bypasses `absoluteFrames` and visibly reconstructs through stale parent-relative transforms.

Do not open a recorder/finalizer production fix solely because a retained pre-#817 sidecar, such as the stale `dc538...` recording, replays with an old Relative `endUT`. Old replayed sidecars are playback validation inputs, not current-main recorder correctness evidence.

Likely fix areas, if needed:

- Recorder/finalizer: extend `DebrisRelativeRecorderPolicy` call coverage to the missing lifecycle path.
- Playback: tighten `DebrisRelativePlaybackPolicy.BuildAuthoredCoverageDiagnostic` or the `GhostPlaybackEngine` callsite that allowed fallback after coverage failure.
- Persistence: ensure `RecordingStore.SaveRecordingFiles` is reached after normalization on the failing path.

## Risk and rollback

- Validation-only plan has no runtime risk.
- Future production fixes in this area are high sensitivity because debris recordings are format-v12 serialized data and many ghosts can play simultaneously.
- Prefer additive guard tests before changing behavior. Do not rewrite legacy v11 behavior or live-loop debris contracts while fixing v12 parent-anchored debris.
- Rollback for a speculative production change would be to revert the narrow follow-up commit and keep PR #817's current normalization/shadow-retirement contracts intact.
