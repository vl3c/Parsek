# §7.7 BubbleEntry/Exit Anchor — Implementation Plan

## 0. Pre-flight verification

Before touching code, verify these load-bearing facts in the worktree (all done during planning, listed here for the Implement agent to re-confirm if anything below looks off):

- `AnchorSource.BubbleEntry = 5` and `AnchorSource.BubbleExit = 6` are reserved at `Source/Parsek/Rendering/AnchorCorrection.cs:27-28`.
- `AnchorPriority.Rank` table includes rank-6 entries for both at `Source/Parsek/Rendering/AnchorPriority.cs:33-34`. No table change needed.
- `AnchorPropagator.TryResolveSeedEpsilon` has the deferred-source gate at `Source/Parsek/Rendering/AnchorPropagator.cs:702-708` (returns false for `BubbleEntry|BubbleExit|CoBubblePeer|SurfaceContinuous`). §7.7 work removes BubbleEntry/BubbleExit from that gate; CoBubblePeer + SurfaceContinuous stay.
- `AnchorCandidateBuilder.Compute` has six per-source emitters at `Source/Parsek/Rendering/AnchorCandidateBuilder.cs:65-70`; no Bubble emitter today. Add a seventh.
- `IAnchorWorldFrameResolver` has 4 methods at `Source/Parsek/Rendering/IAnchorWorldFrameResolver.cs:41-75`. Add a 5th.
- `ProductionAnchorWorldFrameResolver` mirrors that interface at `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:25-129`. Add a 5th method.
- `PannotationsSidecarBinary.AlgorithmStampVersion = 4` at `Source/Parsek/PannotationsSidecarBinary.cs:125`. The on-disk `.pann` schema is unchanged (BubbleEntry / BubbleExit fit in the existing type-byte taxonomy bits 0–6), but adding a new candidate emitter changes the byte content for any recording that has `Active|Background ↔ Checkpoint` transitions, so AlgorithmStampVersion must bump 4 → 5 to invalidate stale `.pann` per HR-10.
- `TrackSectionSource` enum at `Source/Parsek/TrackSection.cs:10-15` is the source-of-truth: `Active = 0` (FlightRecorder, focused), `Background = 1` (BackgroundRecorder, loaded+unpacked peer), `Checkpoint = 2` (orbital propagation, never in physics bubble).
- `RenderSessionState.TryEvaluatePerSegmentWorldPositions` at `Source/Parsek/Rendering/RenderSessionState.cs:972` is the FrameTag-aware Phase 6 helper; reusable per the caveats in §3 below.

---

## 1. Detection Algorithm

Walk `rec.TrackSections` once, examine each adjacent pair `(prev, curr)`:

- `prev.source ∈ {Active, Background}` AND `curr.source == Checkpoint` → **BubbleExit** at the boundary UT (`prev.endUT` ≡ `curr.startUT`). Reference position = the LAST physics-active sample, which is `prev.frames[prev.frames.Count - 1]`.
- `prev.source == Checkpoint` AND `curr.source ∈ {Active, Background}` → **BubbleEntry** at the boundary UT. Reference position = the FIRST physics-active sample, which is `curr.frames[0]`.

Edge cases:

- Recording starts on a Checkpoint section: no BubbleEntry emitted.
- Recording ends on a Checkpoint section: no BubbleExit at the very end.
- Two adjacent Active sections (or Active→Background, etc., same source class): no transition → no candidate.
- Sandwiched Active → Checkpoint → Active: emits BOTH a BubbleExit and a BubbleEntry on the Checkpoint section.

Both candidates store on the Checkpoint section's index (per §7.7 "Applies to: the propagation-only segment"):

- BubbleExit: `(UT, BubbleExit, Side.Start)` on Checkpoint section index `i+1`.
- BubbleEntry: `(UT, BubbleEntry, Side.End)` on Checkpoint section index `i`.

This asymmetry vs §7.5 (which lands on the ABSOLUTE side) is intentional; comment in code.

---

## 2. AnchorCandidateBuilder Integration

File: `Source/Parsek/Rendering/AnchorCandidateBuilder.cs`.

Add `EmitBubbleEntryExitCandidates(Recording rec, Dictionary<int, List<AnchorCandidate>> output)` and wire after `EmitOrbitalCheckpointAndSoiCandidates`:

```text
EmitDockMergeCandidates(...)
EmitSplitCandidates(...)
EmitRelativeBoundaryCandidates(...)
EmitOrbitalCheckpointAndSoiCandidates(...)
EmitBubbleEntryExitCandidates(rec, perSection);   // NEW — §7.7
EmitSurfaceContinuousMarkers(...)
EmitLoopMarkers(...)
```

Implementation:

- Null-guard `rec.TrackSections`.
- `for (int i = 0; i < rec.TrackSections.Count - 1; i++)`.
- `aPhys = a.source == Active || a.source == Background`. Same for `bPhys`.
- `aCk = a.source == Checkpoint`. Same for `bCk`.
- `aPhys && bCk` → BubbleExit on `i+1`, Side.Start.
- `aCk && bPhys` → BubbleEntry on `i`, Side.End.
- Otherwise: no emit.

Extend the per-source counters in `BuildAndStorePerSection` (lines 172, 206-213): add `bubbleEntry` / `bubbleExit` counters and corresponding `case AnchorSource.BubbleEntry|BubbleExit` arms.

Per-candidate Verbose at lines 167-171 is source-agnostic — BubbleEntry/Exit get the format for free.

---

## 3. World-Frame Resolver

### 3.1 Interface addition

`Source/Parsek/Rendering/IAnchorWorldFrameResolver.cs` — append:

```csharp
/// <summary>
/// §7.7 BubbleEntry / BubbleExit world-frame reference. The
/// <paramref name="boundaryUT"/> is the seam UT shared by the
/// physics-active (Active|Background) and propagation-only (Checkpoint)
/// adjacent sections. The resolver reads the LAST physics-active
/// sample of the prev section (BubbleExit, side=Start on the Checkpoint
/// segment) or the FIRST physics-active sample of the next section
/// (BubbleEntry, side=End on the Checkpoint segment) and converts it
/// to world-frame via the section's FrameTag dispatch.
/// </summary>
bool TryResolveBubbleEntryExitWorldPos(
    Recording rec, int sectionIndex, AnchorSide side,
    double boundaryUT, out Vector3d worldPos);
```

### 3.2 Production resolver

`Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs`:

1. Standard guards (null rec, null TrackSections, sectionIndex out of range).
2. `physIdx = sectionIndex - 1` for Side.Start (BubbleExit), `sectionIndex + 1` for Side.End (BubbleEntry).
3. Range-check + verify `phys.source ∈ {Active, Background}`. Return false on mismatch.
4. Pick reference: `frames[Count-1]` (Exit) or `frames[0]` (Entry). Null-guard.
5. Convert by `phys.referenceFrame`:
   - `Absolute` → `body.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude)`.
   - `Relative` → defer with Verbose `bubble-entry-exit-relative-section-deferred`. Return false. (Rationale: RELATIVE+physics-active+adjacent-to-Checkpoint is uncommon; defer for v0.9.1.)
   - `OrbitalCheckpoint` → impossible by construction; return false defensively.
6. `IsFinite(worldPos)` check.

### 3.3 Propagator dispatch

`Source/Parsek/Rendering/AnchorPropagator.cs`:

1. Remove `BubbleEntry|BubbleExit` from deferred-source gate (lines 702-708):
   ```csharp
   if (cand.Source == AnchorSource.CoBubblePeer
       || cand.Source == AnchorSource.SurfaceContinuous)
   {
       return false;
   }
   ```
2. Add switch arms (lines 721-739):
   ```csharp
   case AnchorSource.BubbleEntry:
   case AnchorSource.BubbleExit:
       ok = resolver.TryResolveBubbleEntryExitWorldPos(rec, sectionIndex, cand.Side, cand.UT, out worldRef);
       if (ok) resolvedBubble++;
       break;
   ```
3. Add `int resolvedBubble = 0` alongside other counters at line 240. Thread `ref resolvedBubble` through `TryResolveSeedEpsilon` signature.
4. Append `resolvedBubble={X}` to summary log at lines 649-660.
5. **Update `TryEvaluateSmoothedWorldPos` for Checkpoint sections (line 775)**: when `section.referenceFrame == OrbitalCheckpoint`, evaluate Kepler position via `TrajectoryMath.FindOrbitSegment(section.checkpoints, ut)` + `new Orbit(...).getPositionAtUT(ut)`. Reuse the math from `TryResolveCheckpointSideWorldPos` — factor a shared helper if convenient or duplicate the ~8 lines. Without this, ε would be 0 (Checkpoint sections have no spline) and the Bubble anchor would never produce a real correction.

---

## 4. AnchorPropagator Integration

Already covered in §3.3. BubbleEntry/Exit are intra-recording seeds in `Phase 1 — emit non-DockOrMerge seed anchors` (lines 234-292); DAG walk at lines 297-647 unaffected. §7.11 priority (rank 6) means §7.5 OrbitalCheckpoint wins on hypothetical collision, but real collision is impossible since they land on different sections.

---

## 5. Settings Flag

Existing `useAnchorTaxonomy` flag gates the new emit. No new ConfigurationHash bytes. No new flag.

---

## 6. Logging Contract

- Per-candidate Verbose at commit: free (existing format-string).
- Per-source counts in commit summary: extend with `bubbleEntry={X} bubbleExit={Y}`.
- DAG-walk-summary Info: extend with `resolvedBubble={X}`.
- Anchor ε computed Info per session entry: free (source-agnostic).
- Bubble-radius Warn (HR-9): free (existing 2.5 km sanity in `RenderSessionState`).
- Resolver-miss Verbose: free.
- New `bubble-entry-exit-no-sample` Verbose from inside the resolver when `frames` is null/empty:
  ```
  [VERBOSE][Pipeline-Anchor] bubble-entry-exit-no-sample recordingId={0} sectionIndex={1} side={2} physIdx={3} reason=frames-empty
  ```

---

## 7. Test Plan

All xUnit tests in `[Collection("Sequential")]` mirroring existing patterns.

### 7.1 AnchorCandidateBuilder unit tests (`AnchorCandidateBuilderTests.cs`)

- `EmitsBubbleExit_OnActiveToCheckpointTransition`
- `EmitsBubbleEntry_OnCheckpointToActiveTransition`
- `EmitsBubbleExit_OnBackgroundToCheckpointTransition`
- `EmitsBubbleEntry_OnCheckpointToBackgroundTransition`
- `RecordingStartsWithCheckpoint_NoBubbleEntry`
- `RecordingEndsWithCheckpoint_NoBubbleExit`
- `AdjacentActiveActive_NoBubbleCandidate`
- `AdjacentCheckpointCheckpoint_NoBubbleCandidate`
- `AdjacentBackgroundActive_NoBubbleCandidate`
- `Sandwiched_ActiveCheckpointActive_EmitsBothBubbleEntryAndExit`

May need new helper `MakeSectionWithSource(...)` since existing `MakeSection` hardcodes Active.

### 7.2 World-frame resolver stub tests (`AnchorWorldFrameResolverTests.cs`)

Extend `StubResolver` with `BubbleWorldPos` field + `BubbleCalls` counter. Add:

- `BubbleExit_ResolverHit_WritesEpsilonIntoSessionMap`
- `BubbleEntry_ResolverHit_WritesEpsilonIntoSessionMap`
- `BubbleEntry_ResolverMiss_LeavesEpsilonZeroAndLogsVerbose`
- `BubbleExit_NoSpline_LeavesEpsilonZeroAndLogsVerbose`
- `DeferredSource_BubbleEntry_NoLongerDeferred_ResolverIsCalled` (regression pin for the gate removal)

### 7.3 ProductionAnchorWorldFrameResolver guard-path tests (`ProductionAnchorWorldFrameResolverTests.cs`)

- `BubbleEntryExit_NullRecording_ReturnsFalse`
- `BubbleEntryExit_NullTrackSections_ReturnsFalse`
- `BubbleEntryExit_SectionIndexOutOfRange_ReturnsFalse`
- `BubbleExit_AdjacentPrevSectionIsCheckpoint_ReturnsFalse`
- `BubbleEntry_AdjacentNextSectionFramesEmpty_ReturnsFalseAndLogsNoSample`

### 7.4 AnchorPropagator integration tests (`AnchorPropagationTests.cs`)

- `BubbleEntryExit_DeferredSwitchRemoved_ResolverCalled`
- `BubbleEntryExit_PropagatedSummaryIncludesResolvedBubbleCount`

### 7.5 .pann round-trip extension (`PannotationsSidecarRoundTripTests.cs`)

- `AnchorCandidatesList_RoundTripsBubbleEntryAndBubbleExit`
- `AnchorCandidatesList_DiscardsOnAlgorithmStampVersionBumpToFive`

### 7.6 In-game tests (`RuntimeTests.cs`)

- `Pipeline_Anchor_BubbleExit` (Category `Pipeline-Anchor-BubbleEntry`)
- `Pipeline_Anchor_BubbleEntry`

Single shared synthetic fixture: `pipeline-anchor-bubble-cycle.prec` (Active → Checkpoint → Active).

---

## 8. CHANGELOG + todo doc updates

### 8.1 CHANGELOG.md

Add new bullet under `## 0.9.1` `### Internals`:

> **Ghost trajectory rendering: §7.7 BubbleEntry/BubbleExit anchor.** The §7.7 anchor type now emits real candidates at every recording's `Active|Background ↔ Checkpoint` source-class transition and resolves ε via a new `IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos` method that reads the boundary's last/first physics-active sample as the high-fidelity reference. `.pann` `AlgorithmStampVersion` bumped 4 → 5 so older Phase-6 caches invalidate and re-emit the new candidates on first load (HR-10).

Edit the existing Phase 6 bullet to drop the "§7.7 BubbleEntry/Exit (deferred — needs a physics-active timeline scanner)" parenthetical.

### 8.2 docs/dev/todo-and-known-bugs.md

Strikethrough the §7.7 deferred entry. Note the deferred RELATIVE-frame physics-active section path as a known residual gap.

---

## 9. Risks / Open Questions

### 9.1 First/last physics-active sample interpretation
Literal `frames[0]` / `frames[Count-1]`. Recorder source-class transition is itself the debounced signal.

### 9.2 §7.5 vs §7.7 priority
§7.5 wins on collision; real collision impossible (different section indices). Comment in code.

### 9.3 RELATIVE-frame physics-active sections
Deferred with Verbose. Uncommon in practice. Documented residual gap.

### 9.4 §7.7 was a low-complexity deferral
"Physics-active timeline scanner" is just adjacent-section walk. Confirmed.

### 9.5 Cycle / chain-edge interaction
Intra-recording; no DAG edges added. No new cycle vectors.

---

## 10. Step-by-Step Implementation Sequence

### Commit 1 — emitter + resolver + propagator gate removal + tests

1. `IAnchorWorldFrameResolver.cs` — add 5th method.
2. `ProductionAnchorWorldFrameResolver.cs` — implement 5th method + new Verbose.
3. `AnchorCandidateBuilder.cs` — add `EmitBubbleEntryExitCandidates`, wire, extend counters.
4. `AnchorPropagator.cs` — remove gate, add switch arms, `resolvedBubble` counter, Kepler dispatch in `TryEvaluateSmoothedWorldPos` for Checkpoint sections.
5. xUnit tests per §7.1–§7.5.

`dotnet build && dotnet test` — all green.

### Commit 2 — version bump + doc updates + in-game tests

6. `PannotationsSidecarBinary.cs` — bump AlgorithmStampVersion 4→5 + comment.
7. `RuntimeTests.cs` — add `Pipeline_Anchor_BubbleExit` / `_BubbleEntry`.
8. Synthetic fixture builder if needed.
9. `CHANGELOG.md` — new bullet + edit existing Phase 6 bullet.
10. `docs/dev/todo-and-known-bugs.md` — strikethrough.

`dotnet build && dotnet test` — all green.

### Commit messages

No `Co-Authored-By`. Use the suggested messages from the planner.
