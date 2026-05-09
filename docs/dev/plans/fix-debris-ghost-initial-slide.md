# Fix Plan: Debris Ghost Initial Slide

Date: 2026-05-08

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-debris-ghost-initial-slide`

Branch: `fix-debris-ghost-initial-slide`

## Problem

During v12 debris playback, newly visible debris ghosts can render for several frames at an absolute shadow / single-point pose and then pop into the correct relative pose. This is user-visible as debris "sliding in" after spawn/proximity load.

The relevant evidence is in `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-08_2019_latest-after-pr774\KSP.log`, collected from deployed commit `3342ef29` plus PR #771.

## Concrete Evidence

All three cases are `Kerbal X Debris`, anchored to parent recording `33b64687`.

| debris recording | ghost | first bad frames | first corrected frame | pop delta |
| --- | ---: | --- | --- | ---: |
| `078fa8d7` | `#1` | frames `45830-45832`, UT `57.28-57.30`, `mode=SinglePoint`, pos `(-396.28,-3.26,-48.54)` | frame `45833`, UT `57.32`, pos `(-341.93,-2.67,-17.83)` | `62.43 m` |
| `5e7fea0a` | `#3` | frames `45982-45986`, UT `58.58-58.60`, `mode=SinglePoint`, pos `(-478.41,-2.48,-99.65)` | frame `45987`, UT `58.62`, pos `(-432.15,-2.10,-65.15)` | `57.71 m` |
| `0ed97cde` | `#5` | frames `46139-46142`, UT `59.94-59.96`, `mode=SinglePoint`, pos `(-550.97,-2.35,-159.85)` | frame `46143`, UT `59.98`, pos `(-512.64,-2.28,-120.70)` | `54.79 m` |

Representative resolver warning: `relative-anchor-unresolved: reason=anchor-out-of-recorded-range recordingId=33b64687... sectionIndex=(none) ut=57.280000000003355`.

All three concrete cases have matching `RELATIVE recorded-anchor fallback to absolute shadow` warnings in the log:

- `078fa8d7`: recording `#1`, `frames=49`, `sectionUT=[57.3,71.4]`.
- `5e7fea0a`: recording `#3`, `frames=42`, `sectionUT=[58.6,71.7]`.
- `0ed97cde`: recording `#5`, `frames=35`, `sectionUT=[59.9,70.8]`.

## Root Cause

This is one failure mode with two contributing layers.

1. Recorder/data coverage: the parent recording has tiny section gaps at breakup UTs. For example, one section ends at `57.28`, while the next parent section starts around `57.32`. Playback UT carries epsilon (`57.280000000003355`), and `TrajectoryMath.FindTrackSectionForUT` uses exclusive `endUT` for non-last sections, so the lookup returns `-1` inside the gap.

2. Resolver/render fallback: `RelativeAnchorResolver.TryResolveRecordingPose` treats the section miss as unresolved after same-chain continuation fails. It does not use the parent recording's flat point interpolation path, even though the parent ghost renders successfully at the same UT via `mode=PointInterp pointFrameSource=flat-points sectionIndex=-1`.

   The log path for the concrete `078fa8d7` slide is:

   - `relative-anchor-unresolved ... sectionIndex=(none) ut=57.280000000003355`
   - `RELATIVE recorded-anchor fallback to absolute shadow: recording #1 ... frames=49 sectionUT=[57.3,71.4]`
   - `mode=SinglePoint ... playbackUT=57.800 ... final=(-396.28,-3.26,-48.54)`

   `mode=SinglePoint` is emitted by `PositionGhostAt`, but that does not by itself prove the engine-level `GhostPlaybackEngine.PositionAtPoint(traj.Points[0])` fallback. `TryUseRelativeAbsoluteShadowFallback` calls `InterpolateAndPosition` on `target.Section.absoluteFrames`, and when the target UT is before the first absolute shadow frame that path also calls `PositionGhostAt`, producing the same `mode=SinglePoint` trace. The adjacent `RELATIVE recorded-anchor fallback to absolute shadow` warning makes the absolute-shadow path the attributed route for this log case. Engine-level `PositionAtPoint` remains a route to guard in tests, but is not the primary evidenced branch here.

3. Fallback data gap: the fallback `playbackUT=57.800` while the Relative section starts at `57.280` shows the absolute-shadow/flat fallback list starts about `0.52 s` after section start. The Relative section frames themselves do include a section-start seed (`RecordedRelative` later logs `beforeUT=57.280 afterUT=57.800`). If the resolver compatibility fix works, playback should never need the absolute-shadow fallback for these frames; if a reproduced test still slides after the resolver fix, the recorder-side fix must also seed section-start absolute shadow / flat fallback data, not only close parent section metadata gaps.

`AnchorCorrection` is relevant as a pattern but not as a literal fix. The Re-Fly correction stores and applies a translation epsilon after the intrinsic pose resolves. Here, the anchor pose does not resolve at all for several frames, so the useful payload would be a full known-good anchor pose or a safe flat-points fallback, not an epsilon.

## Goals

- Existing v12 debris recordings with tiny parent section gaps render their first visible frame from the same anchor pose the parent can render at that UT.
- True out-of-range anchors still fail; do not hide real missing-data bugs.
- Do not reinterpret Relative-section local-offset samples as body-fixed lat/lon/alt.
- Preserve legacy v11 debris absolute-shadow behavior.
- Stop creating this gap class in new recordings once the recorder path is understood.
- Cover flight-scene playback paths that resolve Relative sections through `RelativeAnchorResolver`.

## Non-Goals

- Do not redesign `AnchorCorrection`.
- Do not change debris relative-local-offset semantics.
- Do not remove the existing absolute-shadow fallback; keep it for cases where no safe anchor pose exists.
- Do not fix map/tracking-station/KSC Relative rendering in this plan. Current code search finds `RelativeAnchorResolver` call sites only in `ParsekFlight.cs`; non-flight Relative rendering needs a separate probe if it exists or is missing.

## Proposed Fix Surface

### 1. Renderer Compatibility Fix

Primary function: `RelativeAnchorResolver.TryResolveRecordingPose`.

When `FindTrackSectionForUT(recording.TrackSections, ut)` returns `-1`, add a guarded gap fallback before warning `anchor-out-of-recorded-range`.

Proposed helper:

```csharp
private static bool TryResolveSmallSectionGapPose(
    RelativeAnchorResolverContext context,
    Recording recording,
    double ut,
    out AnchorPose pose)
```

Behavior:

- Detect only proven intra-recording gaps: finite previous and next sections in the same recording, `prev.endUT - 1e-6 <= ut <= next.startUT + 1e-6`, positive `next.startUT - prev.endUT`, and gap width below a conservative threshold.
- Threshold rule: use `min(0.10 s, max(0.05 s, 3 * sampleCadenceSeconds))` when cadence is available; otherwise use `0.10 s`. The observed parent gaps are about `0.04 s` and the trace cadence is about `0.02 s`, so this allows about three frame intervals while hard-capping the compatibility window.
- Resolve the pose from safe flat points:
  - If the recording contains any Relative sections, require `TrajectoryTextSidecarCodec.TryBuildAbsoluteShadowFlatPointsForRelativeSections(recording, out safeRelativeFlatPoints)` and fail closed if it cannot build safe absolute shadows.
  - Use raw `recording.Points` only for recordings whose sections are known absolute/body-fixed.
  - Reuse `TryResolveAbsoluteFramesPose` for interpolation and world pose construction.
- Require a local bracket around `ut`, or an exact frame at `ut`, in the selected safe flat point list. Exact means `abs(point.ut - ut) <= 1e-6`. Bracket means two consecutive samples `before.ut <= ut <= after.ut` with `after.ut - before.ut <= threshold`. Do not accept "first point <= ut <= last point" coverage alone, because that can interpolate across a much larger unrelated interval.
- Emit a distinct verbose log, for example `anchor-gap-flat-points-fallback`, with recording id, UT, previous section, next section, and gap width. Rate-limit by `(recordingId, prev.endUT, next.startUT)` so a ghost sitting in the gap for several frames emits once per gap window, not once per frame.
- If the helper fails, continue to existing same-chain continuation and unresolved warning paths.

Ordering note: use the gap helper before same-chain continuation only when the miss is a proven small intra-recording gap. For an end-of-recording miss with no next section, keep same-chain continuation first.

Field note: resolver lookup uses section-level `TrackSection.anchorRecordingId` (`RelativeAnchorResolver.TryResolveSectionAnchorRecordingId` / `TryResolveRelativeSectionPose`), not top-level `Recording.DebrisParentRecordingId`. `DebrisParentRecordingId` is a v12 parent-correlation / recorder-dispatch field; tests must assert the section-level anchor field is populated.

### 2. Recorder Prevention Fix

Primary investigation targets:

- `FlightRecorder.StopRecordingForChainBoundary`
- `FlightRecorder.ResumeAfterFalseAlarm`
- `FlightRecorder.RestoreTrackSectionAfterFalseAlarm`
- `ParsekFlight.ProcessBreakupEvent`
- `BackgroundRecorder.InitializeLoadedState`
- `StartBackgroundTrackSection`
- `AppendFrameToCurrentTrackSection`
- the code that closes the parent section and opens the post-breakup continuation section

The parent active-recording stop/resume seam must be answered before implementing the recorder change. The observed gap matches a parent section closing at the breakup UT and reopening from a later `Planetarium.GetUniversalTime()`, rather than a child debris recorder problem alone. Confirm whether the restart is intentionally deferred by one frame before changing seam timestamps.

Desired invariant: adjacent sections produced by breakup handling should not leave an uncovered UT interval. Either:

- write the next section's `startUT` equal to the previous section's `endUT`, or
- append/carry a seam sample so both section metadata and frame coverage are continuous.

This should be done after reading the exact breakup section emit path, because the open question is whether the gap comes from a missed physics sample, deferred child registration, or a deliberate Absolute-to-Relative transition delay.

## Test Plan

1. Existing `TrajectoryMath.FindTrackSectionForUT` boundary tests must still pass. The compatibility fix should live above section lookup miss handling and must not broaden normal section membership.

2. Unit test `RelativeAnchorResolver.TryResolveRecordingPose` with a synthetic anchor recording:
   - two absolute sections with a `0.04 s` metadata gap,
   - flat points bracketing a UT inside the gap,
   - local bracket span below the small-gap threshold,
   - assert resolution succeeds through the new fallback, logs `anchor-gap-flat-points-fallback`, and does not emit `relative-anchor-unresolved`.

3. Unit test relative safety:
   - an anchor recording whose `Points` are not safe body-fixed absolute points must not be used directly as lat/lon/alt;
   - the resolver either uses `TryBuildAbsoluteShadowFlatPointsForRelativeSections` or fails.

4. Unit test mixed relative coverage:
   - mixed Relative/Absolute anchor sections succeed in the gap only when safe absolute shadow flat points can be built;
   - they fail closed when only raw Relative local-offset points are available.

5. Unit test true out-of-range:
   - UT before first section, after last section without same-chain continuation, and a wide internal gap still return false and log unresolved.

6. Regression test for debris playback composition:
   - child debris relative section references an anchor whose section lookup misses in a small gap;
   - child section has `TrackSection.anchorRecordingId` set to the parent recording id; `DebrisParentRecordingId` alone is not sufficient;
   - child pose resolves through the resolver compatibility fallback;
   - trace/postcondition shows `mode=RecordedRelative`, no `relative-anchor-unresolved`, no `RELATIVE recorded-anchor fallback to absolute shadow`, and no `mode=SinglePoint` fallback from either `TryUseRelativeAbsoluteShadowFallback` or engine-level `PositionAtPoint`.

7. Recorder-side tests after implementation details are confirmed:
   - synthetic breakup produces contiguous parent sections or covered seam samples;
   - stop/resume after false-alarm split produces adjacent sections with no uncovered UT interval;
   - child debris section start, first Relative frame UT, and any absolute shadow / flat fallback first-frame UT are intentionally aligned or deliberately guarded from section-start playback;
   - if absolute shadow / flat fallback points are retained for v12 debris, they include a section-start seed or otherwise cannot be selected for the section-start compatibility window;
   - existing legacy/v11 debris behavior remains unchanged.

No mandatory in-game test is planned for the renderer fix: the resolver and playback dispatch path are xUnit-coverable with synthetic recordings. A runtime smoke pass is still useful after implementation because the original symptom is visual, but it should not be the only proof.

## Risks

- A too-large gap threshold could mask genuine missing-data intervals. Keep the threshold small, log the fallback, and require both neighboring sections.
- Flat-points fallback can be dangerous if it reads Relative local offsets as lat/lon/alt. The helper must reuse the existing safe-shadow conversion path where possible.
- Recorder-only fix is insufficient for existing saves; renderer compatibility is required.
- Renderer-only fix leaves future logs noisy and permits new gaps; recorder prevention should follow.

## Implemented Decisions And Follow-Ups

- Renderer compatibility landed in `RelativeAnchorResolver.TryResolveRecordingPose` as the guarded small-gap fallback described above.
- Recorder prevention landed in `FlightRecorder.RestoreTrackSectionAfterFalseAlarm`: reopened sections start at the payload seed UT, and discarded zero-frame sections no longer hide the last persisted payload section during resume.
- Remaining follow-ups stay tracked in `docs/dev/todo-and-known-bugs.md`: generic section-boundary endUT epsilon handling and sparse relative-sample debris discontinuities.
- Threshold tuning under unusual high-warp / low-fps sampling remains a telemetry question; the implementation keeps the hard `0.10 s` cap and logs each fallback window.

## Review Notes

A GPT-5.5 xhigh review of this plan agreed with renderer-first ordering and the narrow two-part surface: `RelativeAnchorResolver` compatibility, then recorder prevention at the parent `FlightRecorder` split/resume seam. The review's required tightenings have been incorporated above:

- fail closed rather than using raw `recording.Points` for Relative-section data;
- require a local bracketing pair or exact frame around the gap UT;
- avoid changing `TrajectoryMath.FindTrackSectionForUT`, relative point semantics, or the generic `ParsekFlight` absolute-shadow fallback;
- start recorder investigation at `StopRecordingForChainBoundary` / `ResumeAfterFalseAlarm` / `RestoreTrackSectionAfterFalseAlarm`.
