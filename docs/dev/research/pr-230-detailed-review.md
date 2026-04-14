# PR #230 detailed review — Phase 11.5 recording storage optimization

**Context:** second-pass review after the light pass flagged #230 as "needs another look" (see todo #365). Focused on `RecordingOptimizer`, `SessionMerger`, `RecordingStore`, and the new `TrajectorySidecarBinary` format — these are the high-risk areas for ghost-lookup / `R`/`FF` regressions.

**Scope reviewed so far:**
- `Source/Parsek/RecordingOptimizer.cs` — full diff
- `Source/Parsek/SessionMerger.cs` — full diff (small)
- `Source/Parsek/RecordingStore.cs` — diff + current `SaveRecordingFilesToPathsInternal` on head
- `Source/Parsek/Recording.cs` — new `StartUT`/`EndUT` unified bounds + `GhostSnapshotMode` enum
- `Source/Parsek/TrajectorySidecarBinary.cs` — header + probe (not yet: sparse v3 point encoding, reader bounds)

Not yet reviewed: `BackgroundRecorder`, `PartStateSeeder`, `ParsekFlight` integration, `RecordingTree`, test fixtures, in-game runtime tests.

---

## Findings: latent bugs fixed by this PR (not just storage)

The PR description soft-pedals how much of this is bug fixes rather than format work. The optimizer/merger fixes below are real and load-bearing.

1. **`RecordingOptimizer.MergeInto` was dropping metadata on absorbed recording.** The new code copies `ChildBranchPointId`, all eight `TerminalOrbit*` fields, `TerminalPosition`, `TerrainHeightAtEnd`, and `SurfacePos` from the absorbed recording to the target when the absorbed side has them. Previously these were silently lost every time the optimizer merged two adjacent segments. Clear data-correctness fix.

2. **`RecordingOptimizer.SplitAtSection` was not invalidating cached stats on the new `second` recording.** Only `original.CachedStats = null` was set; `second.CachedStats` inherited whatever the last compute pass produced for the full pre-split recording. Any stat reader that consumed `second` between split and the next recompute saw stale values. Clear bug fix.

3. **`RecordingOptimizer.TrimBoringTail` was lying about trims.** The old code set `sec.endUT = trimUT` for the boundary section without pruning frames past `trimUT`. Points past the trim survived in `TrackSections[i].frames` and would come back as soon as the flat cache was rebuilt from sections. The new `TryTrimTrackSectionPayload` actually removes frames / orbital checkpoints past `trimUT`. This is the most significant latent bug in the bundle — it means prior trim passes were not reducing storage and could re-inject already-trimmed data through any flat-cache rebuild.

4. **Tree state was not synced when the optimizer merged recordings.** New `RecordingStore.UpdateTreeStateAfterOptimizationMerge` updates the tree's `Recordings` dict, `RootRecordingId`, `ActiveRecordingId`, and `BranchPoints[].ParentRecordingIds` when the optimizer removes `absorbed`. Previously the optimizer only removed `absorbed` from the flat `committedRecordings` list and left the tree pointing at a dead id. Easy way to produce a dangling branch point parent reference after any optimizer pass. Clear bug fix.

5. **`RecordingOptimizer.TrimBoringTail` now resyncs flat trajectory from sections after trimming** (`TrySyncFlatTrajectoryFromTrackSections(rec, allowRelativeSections: true)`). Matches finding #3 — now that the section trim is real, the flat cache must follow.

## Findings: new format is well-guarded

6. **Section-authoritative write is gated by `HasCompleteTrackSectionPayloadForFlatSync`.** A recording only writes in section-authoritative mode if every section has usable payload (`Absolute` / `Relative` sections with `frames.Count > 0`, `OrbitalCheckpoint` with `checkpoints.Count > 0`). A single empty section forces the flat-fallback path. Correct fail-safe.

7. **Explicit `sectionAuthoritative` header on disk** disambiguates section-authoritative from legacy v0 files. When missing, falls back to inferring from "zero top-level POINTs and zero ORBIT_SEGMENTs". Backward-compatible.

8. **`RebuildPointsFromTrackSections` dedupes boundary copies** between adjacent sections using `TrajectoryPointEquals` / `OrbitSegmentEquals`. Safe because all serialization uses `"R"` format and sections share the same underlying structs — reference-equal post-round-trip in practice.

9. **`DeserializeTrajectoryFrom` is now a branch on the header**, no double-call of `DeserializeTrackSections` — section-authoritative path clears sections first, then deserializes; flat-fallback path deserializes flat lists + sections once at the end. Confirmed by reading the diff carefully.

## Findings: epoch / mode rollback on save failure

10. **`SaveRecordingFilesToPathsInternal` uses staged writes with full rollback.** Current head pattern:

```csharp
int originalSidecarEpoch = rec.SidecarEpoch;
GhostSnapshotMode originalGhostSnapshotMode = rec.GhostSnapshotMode;
var changes = new List<StagedSidecarChange>();
try {
    rec.SidecarEpoch++;
    changes.Add(StageSidecarWrite(path => WriteTrajectorySidecar(path, rec, rec.SidecarEpoch), precPath));
    changes.Add(StageSidecarWrite(..., vesselPath));
    changes.Add(StageSidecarWrite(..., ghostPath));
    ApplyStagedSidecarChanges(changes);  // commits all or throws
    ...
}
catch (Exception ex) {
    CleanupStagedSidecarArtifacts(changes, committed: null);
    rec.SidecarEpoch = originalSidecarEpoch;
    rec.GhostSnapshotMode = originalGhostSnapshotMode;
    return false;
}
```

This is more robust than the PR body hinted. On any failure — trajectory write, vessel write, ghost write — the temp files are cleaned up, the epoch is restored, and the ghost-snapshot-mode is restored. **Important caveat:** the two-phase staging may have been added later (possibly #252 readable mirrors), not by #230 itself. Functionally the current code is correct; if this was added after #230, then #230 on its own shipped with a weaker rollback story. Worth a `git log -p` spot-check if anyone cares about the historical shape.

## Minor concerns / smells

- **`StartUT`/`EndUT` getters now do a three-source scan on every access** (`Points`, `OrbitSegments`, `TrackSections`) plus a linear scan through sections to find first/last playable. Called from many places per frame. If any hot path reads these, could show up as allocation-free CPU; worth a quick diagnostics pass. Defensive caching would be trivial if it bites. Not a correctness issue.

- **`ExplicitStartUT` / `ExplicitEndUT` can only extend, never shrink** computed bounds. Explicit comment in PR #230 plan doc says "explicit is a floor/ceiling of the actual trajectory" — code matches that intent. Confirmed: `if (ExplicitStartUT < startUT) return ExplicitStartUT;` and `if (ExplicitEndUT > endUT) return ExplicitEndUT;`.

- **`ShouldReadSectionAuthoritativeTrajectory` inference fallback is "no POINT nodes and no ORBIT_SEGMENT nodes".** A hand-edited or truncated sidecar (header missing, flat nodes missing, sections present) would be mis-read as section-authoritative. Not a realistic field failure but a sharp edge.

- **`HasCompleteTrackSectionPayloadForFlatSync` returns `false` on any single empty section**, which means a recording mid-save with one freshly-opened empty TrackSection will always fall back to the flat-list path on disk. Probably correct, but if an empty continuation section is a normal shape for "just committed, nothing recorded yet" branch tips, those will quietly write flat-only even when `RecordingFormatVersion >= 1`. Worth a diagnostics check: grep the verbose logs for "used flat fallback path" on recordings that should be section-authoritative.

- **`TrajectoryPointEquals` / `OrbitSegmentEquals` use exact float equality.** Safe today because serialization is lossless (`"R"` format for text, exact `Write`/`Read` for binary v2/v3), but any future lossy codec would silently break boundary dedupe and inflate flat-cache rebuild size. Worth a unit test that round-trips through each codec and asserts `TrajectoryPointEquals` on boundary pairs.

## Still to check (next pass)

- `TrajectorySidecarBinary` reader bounds / sparse v3 point encoding: verify reads respect stream length, handle truncated files gracefully, and that the sparse flags round-trip losslessly for the "body default + funds override" mixed case.
- `BackgroundRecorder` flat-cache maintenance: does the background path produce the same section/flat shape the optimizer expects? The PR lists "background recorder + optimizer consistency" as an in-scope fix.
- `PartStateSeeder` interaction with the trimmed track sections (used for debris start-pose seeding per #264 follow-up).
- End-to-end round-trip coverage in `RecordingStorageRoundTripTests`: which of (v0 flat / v1 flat+sections duplicated / v1 section-authoritative / v2 / v3 / alias-mode snapshot) pairs have save+load tests that assert equality?
- Zero-throttle debris engine playback path: find it in the diff and verify it is not gated on sidecar format version.

## Verdict so far

No visible bugs. The "needs another look" flag was driven by scope and interlock risk, not by any specific smell. The optimizer fixes in the bundle are real bug fixes that were probably found while touching this area and should be called out more prominently in the changelog for future bisect value. Next pass should focus on the binary reader, background-recorder flat-cache consistency, and round-trip coverage matrix.
