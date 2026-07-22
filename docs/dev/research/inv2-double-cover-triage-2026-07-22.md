# INV2-NO-DOUBLE-COVER triage: B5 Mun-impact run, 2026-07-22

Status: TRIAGED - REAL Parsek defect (low severity), NOT a regression of PR #1304.

## The finding, reproduced exactly

Reproduced offline with `scripts/analyze-recordings.ps1 -SaveDir <collect>/saves/b2-lko-craft -NoBuild`
over the crash-run collect `logs/2026-07-22_0355_B5-mun-flyby` (the collect for harness
result `2026-07-22_0024_B5-mun-flyby.json`; the harness log maps that run's collect to
the 0355 folder). Header: `FAIL=3 WARN=1 INFO=0 STALE=0 BASELINED=0 RED=1`.

```
FAIL INV2-NO-DOUBLE-COVER target=145a449e028449c9a8e28d66bac19dad#4
  INV2 overlap recording=145a449e... a=[39.380000000000557,48.480000000001979] b=[48.460000000001976,64.120000000004381]
FAIL INV2-NO-DOUBLE-COVER target=145a449e028449c9a8e28d66bac19dad#5
  INV2 overlap recording=145a449e... a=[48.460000000001976,64.120000000004381] b=[64.100000000004385,83.1600000000006]
FAIL INV2-NO-DOUBLE-COVER target=145a449e028449c9a8e28d66bac19dad#6
  INV2 overlap recording=145a449e... a=[64.100000000004385,83.1600000000006] b=[83.1400000000006,212.17989089964362]
```

Recording `145a449e028449c9a8e28d66bac19dad` is the main "Kerbal X" flight recording
(58 TrackSections, UT 34.56 -> 22349.62, terminal=Destroyed at the Mun impact). The
overlapping surface is TrackSection `[startUT,endUT]` spans of ordinary physical
sections (env=Atmospheric, ref=Absolute, source=Active). Each overlap is exactly one
physics tick (0.02 s; 0.04 s in one sibling run) and sits at a booster-separation
boundary during ASCENT (UT ~48.5, ~64.1, ~83.2), NOT at the crash (~UT 22350). The
sibling red runs (results 0135 and 0206, collects 0506 and 0537) show the identical
three-boundary pattern; the green runs (0055_a2 / 0249 / 0958 / 1018 / 1052, e.g.
collect 0426) show the same boundaries stitched exactly (`end(A) == start(B)`).

The WARN in the offline rerun (`INV9 missing-rewind-save`) is a collect-layout
artifact (the collect stores rewind saves under `parsek/Saves`, not the save dir) and
is unrelated.

## Root cause

Two cooperating pieces, both confirmed in KSP.log of the red run:

1. Producer of the overlap (by design, expected to be reconciled later):
   every debris-only staging separation runs the deferred joint-break path
   (`ParsekFlight.HandleJointBreakDeferredCheck` -> `StopRecordingForChainBoundary`
   -> classification=DebrisSplit -> `ResumeSplitRecorder` -> false-alarm resume).
   - `FlightRecorder.FinalizeRecordingState` closes the active section with
     `CloseCurrentTrackSection(Planetarium.GetUniversalTime())` (FlightRecorder.cs
     ~7440), so the closed section's `endUT` is the STOP-frame UT, which overhangs
     the section's last recorded frame by up to one sample gap (last frame 48.46,
     endUT 48.48).
   - `FlightRecorder.RestoreTrackSectionAfterFalseAlarm` (FlightRecorder.cs
     ~7931-7947) then reopens the continuation section and back-aligns its
     `startUT` to the boundary-seed UT (the closed section's last frame UT) and
     seeds a duplicate frame there, for playback continuity:
     `ResumeAfterFalseAlarm: aligning reopened TrackSection startUT from 48.500 to
     boundary seed UT=48.460`.
   - Net: reopened.startUT (48.46) < closed.endUT (48.48) inside ONE recording.
     This interior overlap exists IN MEMORY in every run, red and green alike
     (the green run logs the identical align lines at 48.48/48.52).

2. The escape path (why it reached disk only in the crashed runs):
   the overlap is normally healed at commit. `RecordingStore.CommitTree` ->
   `ApplySessionMergeToRecordings` -> `SessionMerger.MergeTree` resolves it - the
   green run logs `MergeTree: vessel='Kerbal X' inputSections=58 outputSections=57
   overlapsResolved=4` and its sidecar comes out clean. In the crashed runs the
   vessel was destroyed at the Mun impact BEFORE the harness's CommitTree command:
   the destroyed-vessel flow ran `FinalizeTreeRecordings` -> `StashPendingTree`
   (state=Finalized), deferring the post-destruction merge dialog "to post-report
   scene transition"; the seam `CommitTree` then failed with
   `committree no-active-tree` (verdict=ERROR) and `FlushAndQuit` saved the game.
   OnSave persisted the PENDING tree's recordings raw - `SessionMerger.MergeTree`
   never ran - so the un-reconciled sections landed in the `.prec` sidecar, and the
   analyzer correctly flagged them. The write-path
   `EnsureCheckpointSectionsForTopLevelOrbitSegments` pass ran but it only clips
   CHECKPOINT sections, not physical-vs-physical overlaps.

Determinism: this is NOT a flake. Red iff the vessel dies before a successful
CommitTree (all three Mun-impact runs red; all runs where the vessel survived to
commit green). The overlap magnitude equals stopUT minus last-sample UT (one or two
ticks depending on where the sparse sampler last landed).

## Verdict

REAL Parsek defect, low severity. The recording persisted to the save violates the
disjoint-cover contract INV2 checks (`RecordingOptimizer.IsSplittableEnvOrBodyBoundary`
/ section spans partition the timeline). It is a genuine producer-side inconsistency
(closed-section endUT overhang + back-aligned resume startUT) that the architecture
currently masks at commit time via MergeTree's overlap resolution; any flow that
persists a tree in the pending/stashed state (vessel destroyed and the session ends
before the merge dialog resolves - exactly the crash-mission shape, also reachable in
normal play by quitting after a crash) writes the raw overlap to disk. Playback
ambiguity is 0.02-0.04 s (cosmetically invisible), but the on-disk contract violation
is real and INV2 is doing its job.

NOT a regression of PR #1304: that fix addressed `OrbitSegmentCheckpointBridge`
checkpoint-vs-checkpoint and empty-shell overlaps. These are frame-bearing Active/
Absolute sections produced by the chain-boundary stop/resume seam - a different
producer family. #1304's write-path reconcile deliberately does not clip physical
sections against each other.

## Minimal repro

1. Launch any staged craft (stock Kerbal X) and let its boosters separate
   (debris-only splits -> false-alarm resume; one overlap per separation).
2. Crash the vessel (any destruction before CommitTree).
3. End the session without committing the pending tree (quit from flight).
4. Run `scripts/analyze-recordings.ps1` over the save: INV2-NO-DOUBLE-COVER FAIL,
   one finding per staging boundary, overlap = one physics tick.

## Suggested fix

Producer-side, at the seam (preferred): in
`FlightRecorder.RestoreTrackSectionAfterFalseAlarm` (FlightRecorder.cs ~7931), when
the reopened section's startUT is back-aligned to the boundary-seed UT, also clamp
the just-closed section's `endUT` down to that same UT (its own last frame - nothing
was recorded between the last frame and the stop-frame UT, so no data is lost). The
two sections then touch exactly, which INV2 treats as clean, and MergeTree's
commit-time resolution becomes a no-op for this shape instead of load-bearing.
Alternative/belt-and-braces: run the same section-overlap reconcile MergeTree applies
when persisting a PENDING (stashed) tree at OnSave, so no save state can carry
physical-section double-cover regardless of producer.

Draft entry for docs/dev/todo-and-known-bugs.md:

> INV2-NO-DOUBLE-COVER on crashed-flight saves (2026-07-22, B5 Mun impact, runs
> 0024/0135/0206): every debris-only staging separation leaves a one-tick interior
> TrackSection overlap (closed section endUT = stop-frame UT overhangs its last
> frame; ResumeAfterFalseAlarm back-aligns the reopened section startUT to the
> boundary-seed UT). Normally healed by SessionMerger.MergeTree at CommitTree
> (overlapsResolved=N), but a vessel destroyed before commit routes through
> FinalizeTreeRecordings -> StashPendingTree and OnSave persists the pending tree
> raw, so the overlap reaches the .prec sidecar and the analyzer reds. Fix: clamp
> the closed section's endUT to the boundary-seed UT in
> RestoreTrackSectionAfterFalseAlarm (FlightRecorder.cs ~7931-7947), and/or apply
> MergeTree's section-overlap reconcile when persisting pending trees. Not a #1304
> regression (that was checkpoint-bridge overlaps; this is the physical
> stop/resume seam). Triage: docs/dev/research/inv2-double-cover-triage-2026-07-22.md.

## Evidence index

- Rule: `Source/Parsek/Analyzer/Rules/Inv2NoDoubleCover.cs` (interior overlap of
  TrackSection spans = FAIL; exact touch ok; gaps bridged by OrbitSegments ok).
- Red collect: `logs/2026-07-22_0355_B5-mun-flyby` (run 0024). Sidecar
  `.../Parsek/Recordings/145a449e028449c9a8e28d66bac19dad.prec(.txt)`: sec[3]
  frames end 48.46 / endUT 48.48; sec[4] startUT 48.46 with a seed frame at 48.46.
  KSP.log lines 11092-11136 (close endUT 48.48, align 48.50->48.46, seed 48.46),
  18181-18364 (destroyed -> stash pending -> committree no-active-tree ->
  FlushAndQuit raw OnSave).
- Green collect: `logs/2026-07-22_0426_B5-mun-flyby` (run 0055_a2). Same align
  lines in-memory; KSP.log 18528 `MergeTree ... overlapsResolved=4`; sidecar
  boundaries exact-touch.
- Sibling red collects: `logs/2026-07-22_0506_B5-mun-flyby`,
  `logs/2026-07-22_0537_B5-mun-flyby` (same three boundaries).
- Offline rerun output: scratchpad `inv2-analysis/b2-lko-craft.analysis.txt`
  (RED=1, three INV2 FAILs quoted above).
