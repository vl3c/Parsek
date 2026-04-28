I have enough context. Now I'll write the comprehensive plan.

# Phase 6 Implementation Plan

## Worktree, branch, version

- **Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design`
- **Branch:** `feat/anchor-taxonomy-phase6` off `origin/main` HEAD `d8db60c3` — already created.
- **Parsek version:** `0.9.1` (from `Source/Parsek/Properties/AssemblyInfo.cs`). The CHANGELOG has an open `## 0.9.1` block (Phase 4 already landed under `### Internals`); Phase 6 adds another bullet here and a paragraph under `### Features`.
- **Constraints:** `.pann`-only schema additions; no `.prec` bump; no Phase 5 / 7 / 8 / 9 work; no `Co-Authored-By` lines; never edit the main `Parsek/` checkout; HR-1, HR-7, HR-9, HR-10, HR-15 audited at every step.

---

## 1. Anchor Type Matrix (§7.1–§7.10)

The matrix below distinguishes **commit-time** producers (`AnchorCandidateBuilder`, persisted into the `.pann AnchorCandidatesList` block) from **session-time** producers (`AnchorPropagator` + `RenderSessionState.RebuildFromMarker`, transient in-memory only). It also pins which side of which segment the resulting `ε` lands on, and lists upstream dependencies.

The `.pann` block stores `AnchorCandidate` records per `sectionIndex`: a list of `(UT, AnchorSource)` pairs. The session-time producer reads that list, walks the DAG, and writes `AnchorCorrection` entries into `RenderSessionState`'s `(recordingId, sectionIndex, AnchorSide)` map.

| §    | Type                       | Producer (commit `.pann` vs session) | Reference position formula                                                                                                                                                                                                                              | Lands on                                                                                       | Dependencies                                                                                                                              |
|------|----------------------------|--------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------|
| 7.1  | LiveSeparation             | **Session-time only** (already shipped Phase 2 in `RenderSessionState`)        | `target = live_world_at_spawn + (ghost_abs(t_sep) − live_abs(t_sep))`; `ε = target − P_smoothed`                                                                                                                                                        | `Start` of each ghost sibling's first section after the parent BranchPoint                     | Live KSP `Vessel.GetWorldPos3D()` (HR-15 single read), parent `BranchPoint` from `RecordingTree.BranchPoints`, sibling recordings.        |
| 7.2  | DockOrMerge                | Commit (`AnchorCandidateBuilder` emits candidate at the `BranchPoint.Type ∈ {Dock, Board}` UT). Session-time `AnchorPropagator` resolves ε via the Stage-3b walk when an upstream anchor is propagated through the merge. | Pre-merge side: `End` ε = `ε_upstream + (other_abs(t_m) − this_abs(t_m))` per §9.1. Merged-result side: `Start` ε inherited from the highest-priority parent's `End` ε (priority §7.11).                                                              | `End` of the un-anchored side of the dock; `Start` of the merged-result segment.                | `BranchPoint.Type ∈ {Dock, Board}`, ParentRecordingIds[2], ChildRecordingIds[1], `TargetVesselPersistentId`.                              |
| 7.3  | Undock / EVA / JointBreak  | Commit (`AnchorCandidateBuilder`). Session: propagated.                       | `ε_child_start = ε_parent_end + (child_abs(t_s) − parent_abs(t_s))` per §9.1.                                                                                                                                                                          | `Start` of every child segment; `End` of the parent segment if it terminates at the split.     | `BranchPoint.Type ∈ {Undock, EVA, JointBreak}`, parent + 1..N children.                                                                   |
| 7.4  | RelativeBoundary           | Commit (`AnchorCandidateBuilder` scans for `ABSOLUTE↔RELATIVE` adjacent `TrackSection` boundaries — both `Absolute→Relative` and `Relative→Absolute` directions). Session: ε computed at session-entry per recording.                                  | `ε = P_relativeResolved(boundaryUT) − P_smoothed_ABSOLUTE(boundaryUT)`. The RELATIVE side is exact (per §7.4 of the design doc and `.claude/CLAUDE.md`); the ε belongs to the ABSOLUTE side only. | `End` of the ABSOLUTE section that ends at a RELATIVE start, or `Start` of the ABSOLUTE section that begins at a RELATIVE end.        | `TrackSection.referenceFrame`, recording-format version dispatch via `TrajectoryMath.ResolveRelativePlaybackPosition`. v7 absolute shadow when present. |
| 7.5  | OrbitalCheckpoint          | Commit (`AnchorCandidateBuilder` scans `TrackSection.referenceFrame == OrbitalCheckpoint` boundaries against neighbouring ABSOLUTE sections). Session: ε per session entry.                                                                              | `ε = KeplerEvalAtBoundary − P_smoothed_ABSOLUTE(boundaryUT)`. KeplerEval comes from the `OrbitSegment` adjacent to the boundary (already analytical).                                                                                                | `End`/`Start` of the adjacent ABSOLUTE section.                                                | `OrbitSegment` list on the checkpoint section, `TrajectoryMath` Kepler propagation helpers (already exist).                                |
| 7.6  | SoiTransition              | Commit (`AnchorCandidateBuilder` detects body-change boundaries between consecutive sections — different `bodyName` on the boundary `OrbitSegment` checkpoint). Session: ε per session entry.                                                            | Same as §7.5 but using the post-SOI `OrbitSegment` (in the new body's frame) for the post-side ε and the pre-SOI segment for the pre-side ε. The SOI checkpoint provides anchors on **both** sides (§11).                                              | Both sides of the SOI boundary (Start of post, End of pre).                                    | §7.5 infrastructure plus `BodyName`-change detection across `OrbitSegment` boundaries.                                                    |
| 7.7  | BubbleEntry / BubbleExit   | **Session-time** (depends on physics-active state — only known at session entry, since the live focus and its bubble are session-scoped). Candidate UTs CAN be emitted at commit (last/first physics-active sample per session ambient), but Phase 6 keeps it session-only because the §7.7 trigger wording ties it to "the recording session's physics bubble", not a per-recording fact. | At entry: `ε = first_physicsActive_sample − P_smoothed`. At exit: `ε = last_physicsActive_sample − P_smoothed`. The "physics-active" determination comes from `TrackSectionSource ∈ {Active, Background}` (high-fidelity) → `Checkpoint` (propagation-only) transitions inside the section list. | `Start` of the propagation-only segment after entry; `End` of the propagation-only segment before exit. | `TrackSection.source` field (existing). For Phase 6 this is session-time but does **not** require live game state — only recording metadata. |
| 7.8  | CoBubblePeer               | **NOT IMPLEMENTED IN PHASE 6** — Phase 5 territory (designated-primary blend). Phase 6 reserves the enum value (`CoBubblePeer = 7`, already in `AnchorSource`) so the `.pann` byte layout stays stable, but emits no candidates. | n/a                                                                                                                                                                                                                                                     | n/a                                                                                            | Phase 5 `CoBubbleOffsetTraces`. Documented gap.                                                                                           |
| 7.9  | SurfaceContinuous          | **Infrastructure-only in Phase 6** per the Phase 6 spec (§18 row "Phase 6"). `AnchorCandidateBuilder` emits a candidate at `SurfaceMobile` section start with `AnchorSource.SurfaceContinuous`, marking the section as "needs continuous correction"; the per-frame raycast that resolves ε every frame is Phase 7 work and is NOT wired here. | Phase 7 will define `ε(t) = (current_terrain_height(lat, lon) + recordedGroundClearance) − P_smoothed_altitude(t)`. Phase 6 emits the candidate marker and stops. | The `Start` slot on every `SurfaceMobile` section (so the §6.4 lerp infrastructure recognises the section as "anchored"). | Phase 7 `recordedGroundClearance` (`.prec` v9). Phase 6 ε is `Vector3d.zero` for these candidates — documented in code comments and the Warn line at session entry. |
| 7.10 | Loop                       | Commit (`AnchorCandidateBuilder` detects `Recording.LoopIntervalSeconds > 0` and `LoopAnchorVesselPersistentId != 0` — already on `Recording`). Session: ε per session entry.                                                                            | `ε = (anchor_vessel_pos_at_now + recorded_loop_relative_offset(loop_phase)) − P_smoothed`. For Phase 6, ε is computed once at session entry against the anchor vessel's spawn position; the per-frame phase-driven evaluation reuses the existing loop-playback path. | `Start` of the loop-eligible section.                                                          | `Recording.LoopIntervalSeconds`, `Recording.LoopAnchorVesselPersistentId`. Existing loop resolver.                                       |
| 7.11 | Priority resolver          | Commit (`AnchorCandidateBuilder.SelectWinner`) collapses multiple candidates at the same UT before `.pann` write. Session: re-applied if multiple sources happen to land on the same `(recordingId, sectionIndex, AnchorSide)` slot during propagation. | See §4 below.                                                                                                                                                                                                                                          | Same slot as the winning source.                                                              | The §7.11 priority vector (§4 below).                                                                                                     |

**Distinguishing commit-time vs session-time clearly:**

- **Commit-time deterministic (Phase 6 candidate-list payload):** §7.2, §7.3, §7.4, §7.5, §7.6, §7.9 (marker only), §7.10. These are pure functions of the recording's TrackSections, BranchPoints, and OrbitSegments. They go into `.pann AnchorCandidatesList` and survive across saves.
- **Session-time only:** §7.1 (already shipped, depends on live KSP `Vessel`), §7.7 (depends on which sections were physics-active under the *current* session's ambient — even though the data itself is read-only).
- **Phase-6 placeholder:** §7.8 (Phase 5), §7.9 per-frame raycast (Phase 7).

---

## 2. AnchorCandidateBuilder

**File:** `Source/Parsek/Rendering/AnchorCandidateBuilder.cs` (new).

**Purpose:** A single commit-time/load-time pass over a `Recording` that emits `AnchorCandidate[]` per `(recordingId, sectionIndex)`. Pure function — no live state read, deterministic, idempotent (HR-3, HR-4). The output is what `.pann AnchorCandidatesList` persists.

### 2.1 Types

```csharp
internal readonly struct AnchorCandidate
{
    public readonly double UT;
    public readonly AnchorSource Source;
    public readonly AnchorSide Side;     // Start or End of the section
    // No Epsilon here — ε is session-state, computed at RebuildFromMarker.
    // Candidates are pure annotation data per §17.3.1.
    public AnchorCandidate(double ut, AnchorSource source, AnchorSide side) {...}
}
```

`SectionAnnotationStore` adds a parallel `Dictionary<string, Dictionary<int, AnchorCandidate[]>>` keyed identically to the existing spline dict, with `PutAnchorCandidates(recordingId, sectionIndex, AnchorCandidate[])`, `TryGetAnchorCandidates(recordingId, sectionIndex, out AnchorCandidate[])`, and `RemoveRecording(recordingId)` already wired to clear both maps.

### 2.2 Method signatures

```csharp
internal static class AnchorCandidateBuilder
{
    /// <summary>
    /// Single-pass scan over rec.TrackSections + rec.BranchPoints + rec.OrbitSegments.
    /// Writes the result to SectionAnnotationStore. Mirrors SmoothingPipeline.FitAndStorePerSection
    /// in shape — clear-then-populate, log Verbose summary at the end (HR-10).
    /// </summary>
    internal static void BuildAndStorePerSection(Recording rec);

    /// <summary>
    /// Pure helper for tests + the .pann reader. Returns one list per section index.
    /// </summary>
    internal static List<KeyValuePair<int, AnchorCandidate[]>> Compute(Recording rec);

    // Per-source helpers (each is a private static, called from Compute):
    private static void EmitDockMergeCandidates(Recording rec, List<...> output);     // §7.2 (Dock, Board)
    private static void EmitSplitCandidates(Recording rec, List<...> output);          // §7.3 (Undock, EVA, JointBreak)
    private static void EmitRelativeBoundaryCandidates(Recording rec, List<...> output);  // §7.4
    private static void EmitOrbitalCheckpointCandidates(Recording rec, List<...> output); // §7.5
    private static void EmitSoiTransitionCandidates(Recording rec, List<...> output);     // §7.6
    private static void EmitSurfaceContinuousMarkers(Recording rec, List<...> output);    // §7.9 (marker only)
    private static void EmitLoopMarkers(Recording rec, List<...> output);                 // §7.10
}
```

### 2.3 Single-pass scan structure

The implementation walks each producer in turn; per-source emission is independent so the scan is `O(sections + branchPoints + orbitSegments)` total. Order does not matter — duplicates are reconciled by `SelectWinnerAtUT` (§4) before the per-section list is sorted by UT and frozen.

For each producer:

1. **§7.2 Dock/Merge:** Iterate `rec.BranchPoints`. For each `bp` with `bp.Type ∈ {Dock, Board}` AND `rec.RecordingId ∈ bp.ParentRecordingIds OR bp.ChildRecordingIds`: emit `AnchorCandidate(bp.UT, DockOrMerge, side)`. The `side` is `End` if this recording is a parent (its segment terminates at the dock) and `Start` if this recording is the child (its segment begins at the merged result).
2. **§7.3 Undock/EVA/JointBreak:** Same iteration but for `bp.Type ∈ {Undock, EVA, JointBreak}`. `Start` for children, `End` for parents.
3. **§7.4 RELATIVE boundary:** Walk consecutive `rec.TrackSections[i]` and `rec.TrackSections[i+1]`. Emit a candidate when exactly one of the two sections is `referenceFrame == Relative` and the other is `referenceFrame == Absolute`. The candidate UT is the boundary UT (`sections[i].endUT == sections[i+1].startUT`), and the candidate's section is the **ABSOLUTE-side** index — never the RELATIVE side (§6 below explains why). `Side = End` when ABSOLUTE→RELATIVE transition; `Side = Start` when RELATIVE→ABSOLUTE transition.
4. **§7.5 OrbitalCheckpoint boundary:** Same pattern — emit at boundaries between `OrbitalCheckpoint` and `Absolute` sections, on the ABSOLUTE side. The checkpoint side has analytical Kepler anchors and does not need ε.
5. **§7.6 SOI:** Inside `EmitOrbitalCheckpointCandidates`, additionally check whether the boundary crosses bodies (`OrbitSegment.bodyName` differs from previous OrbitSegment's bodyName, or differs from the adjacent ABSOLUTE section's last frame's `bodyName`). When yes, the candidate's `Source` is `SoiTransition` instead of `OrbitalCheckpoint`. SOI candidates are emitted on **both** sides of the boundary (§11 — anchor on both sides).
6. **§7.9 SurfaceContinuous marker:** Iterate `rec.TrackSections`. For each `section.environment == SurfaceMobile`, emit `AnchorCandidate(section.startUT, SurfaceContinuous, Start)`. Phase 6 stops here; Phase 7 wires the per-frame raycast.
7. **§7.10 Loop marker:** If `rec.LoopIntervalSeconds > 0 && rec.LoopAnchorVesselPersistentId != 0`, emit `AnchorCandidate(rec.StartUT, Loop, Start)` for the first loop-eligible section.

Within each producer, the candidate's UT is preserved exactly as it appears in the source data — no rounding, no interpolation (HR-1).

### 2.4 Persistence into `.pann AnchorCandidatesList`

Per §17.3.1, the block schema is:

```
[..]      AnchorCandidatesList
            count : int32
            entries[count] :
              sectionIndex : int32
              utCount      : int32
              uts          : double[utCount]
              types        : byte[utCount]   (matches AnchorSource)
```

The schema does **not** currently carry `AnchorSide`. Phase 6 needs the side bit (Start vs End) to drive §6.4 multi-anchor lerp. Two options:

- **(A) Pack side into the type byte:** the `AnchorSource` enum has 10 members (0..9) — bit 7 of the byte is free. Use `byte typeWithSide = (byte)((int)source | (side == End ? 0x80 : 0x00))`. Reader masks with `0x7F` for source, tests `& 0x80` for side. **No schema change** to count/array layout; backward-compatible with the §17.3.1 line "matches `AnchorSource` enum" because all valid source values are < 128.
- **(B) Add a parallel `sides : byte[utCount]` array**, requiring a schema bump to `PannotationsBinaryVersion = 2` AND an `AlgorithmStampVersion` bump. Heavier — invalidates every existing `.pann` (Phase 1 splines must re-fit).

**Pick (A).** It keeps `PannotationsBinaryVersion = 1` and only requires `AlgorithmStampVersion` to bump. Document the bit-packing in a one-line comment in `PannotationsSidecarBinary.cs` and in `AnchorCandidate`. Add a constant `AnchorCandidate.EndSideMask = 0x80`.

**Reader/writer changes** in `PannotationsSidecarBinary.cs`:

- `Write(...)`: append a 6th argument `IList<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates`. Replace the existing `writer.Write(0); // AnchorCandidatesList` line with the real block payload. Cap the per-section utCount via `MaxAnchorCandidateEntries = 10_000` (already declared).
- `TryRead(...)`: extend the signature with `out List<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates`. The existing `if (anchorCount != 0)` rejection becomes a real read loop (preserve `MaxAnchorCandidateEntries` cap and the stream-length check via `ValidateCount`).
- Reserved blocks below it (`OutlierFlagsList`, `CoBubbleOffsetTraces`) keep their `count = 0` reserved-zero pattern.

### 2.5 Consumption at session entry

`SmoothingPipeline.LoadOrCompute` already loads `.pann` and populates `SectionAnnotationStore.PutSmoothingSpline`. Phase 6 expands the same call to also read the `AnchorCandidatesList` block and call `SectionAnnotationStore.PutAnchorCandidates(recordingId, sectionIndex, candidates)`.

`RenderSessionState.RebuildFromMarker` is the session-entry hook. Phase 6 adds a new step **after** the existing live-separation anchor write loop:

```
... existing live-separation anchor loop (Phase 2) ...
AnchorPropagator.Run(marker, recordings, treeLookup, surfaceLookup);
```

`AnchorPropagator.Run` reads the candidate list from `SectionAnnotationStore` and writes the resolved `AnchorCorrection` entries back into `RenderSessionState`'s anchor map. See §3.

### 2.6 Lazy fit on missing candidates

If a recording's `.pann` predates Phase 6 (no candidates block / count = 0), the Phase 6 reader still loads the splines but finds zero candidates. `SmoothingPipeline.LoadOrCompute` then calls `AnchorCandidateBuilder.BuildAndStorePerSection(rec)` to populate the in-memory store, and `TryWritePann` rewrites `.pann` with the new candidates. The `AlgorithmStampVersion` bump (see §5) makes this discard-and-recompute path fire automatically for every existing `.pann` on first load after Phase 6 ships — exactly the §17.3.1 lazy-compute contract.

---

## 3. AnchorPropagator

**File:** `Source/Parsek/Rendering/AnchorPropagator.cs` (new).

**Purpose:** DAG walk that converts Phase 6 `AnchorCandidate` entries into resolved `AnchorCorrection` ε values, propagating per §9.1 along `BranchPoint` edges and across recordings via the `RecordingTree.Recordings` map and chain links.

### 3.1 Method signatures

```csharp
internal static class AnchorPropagator
{
    /// <summary>
    /// Production entry point. Resolves trees and recordings from RecordingStore + the marker.
    /// Walks every tree's BranchPoint DAG starting from the live anchors written by Phase 2,
    /// then visits every other anchor candidate emitted at commit-time.
    /// </summary>
    internal static void Run(ReFlySessionMarker marker);

    /// <summary>
    /// Test-friendly overload. All side inputs injected — no FlightGlobals / RecordingStore reads.
    /// </summary>
    internal static void Run(
        ReFlySessionMarker marker,
        IReadOnlyList<Recording> recordings,
        IReadOnlyList<RecordingTree> trees,
        Func<string, double, double, double, Vector3d> surfaceLookup);

    /// <summary>
    /// Pure-static §9.1 propagation rule. Public for unit tests.
    ///   ε' = ε + (recordedOffsetAtEvent − smoothedOffsetAtEvent)
    /// </summary>
    internal static Vector3d Propagate(
        Vector3d epsilonUpstream,
        Vector3d recordedOffsetAtEvent,
        Vector3d smoothedOffsetAtEvent);
}
```

### 3.2 Walk structure

The walk is **forward-only in trajectory time** — anchors propagate along DAG edges from lower UT to higher UT. The DAG is acyclic by construction (HR-13), so no cycle protection is needed beyond a `visited` set keyed by `(recordingId, sectionIndex, AnchorSide)` to avoid revisiting an edge.

Pseudocode for `Run`:

```
1. Collect seed anchors:
     a. Phase 2 LiveSeparation anchors already written to RenderSessionState (do NOT re-emit them; they are the seeds).
     b. Every (recordingId, sectionIndex, side, source) candidate from SectionAnnotationStore where the source is
        commit-time-deterministic (Dock/Merge, Undock/EVA/JointBreak, RelativeBoundary, OrbitalCheckpoint,
        SoiTransition, Loop). Resolve each into an AnchorCorrection (§3.3) and add to RenderSessionState if no
        higher-priority ε already occupies the slot (§7.11 §4 below). Each of these is also a propagation seed.

2. Build a queue of edges: every BranchPoint visible to the trees in scope. An edge is (parentRec, parentSection,
   childRec, childSection, branchPoint).

3. While the queue is non-empty:
     a. Pop edge (P→C, bp).
     b. If C is suppressed (SessionSuppressionState.IsSuppressed(C.RecordingId)) → skip the edge (§9.4).
        Emit Pipeline-AnchorPropagate Verbose "suppressed-predecessor" line, increment skipped counter.
        Do NOT propagate further into the suppressed subtree.
     c. Else if RenderSessionState already has a higher-priority ε at (C.recordingId, C.sectionIndex, Start) →
        skip propagation into C (§7.11 winner already there). Continue traversing C's downstream edges anyway,
        because C's ε is what propagates downstream regardless of how it got there.
     d. Else:
        - Look up parent's End ε at (P.recordingId, P.sectionIndex, End). If present, ε_upstream = that.
          If absent, ε_upstream = (Start ε of P) — the constant case from §6.4 row 1.
          If neither, ε_upstream = Vector3d.zero (§6.4 row 4).
        - Compute recordedOffsetAtEvent and smoothedOffsetAtEvent at bp.UT using the §3.4 helper.
        - ε_C_start = Propagate(ε_upstream, recordedOffset, smoothedOffset).
        - Insert at (C.RecordingId, C.SectionIndex, Start) with Source = mapped from bp.Type:
            Dock/Board → DockOrMerge
            Undock/EVA/JointBreak → respective Phase-6 source byte (DockOrMerge for merges, the un-aliased
            split sources do not have distinct enum values today — §7.3 aliases all of them under
            "structural-event-derived ε"; document this).  Phase 6 does NOT add new enum values.
            We map all three split causes to AnchorSource.DockOrMerge for now since the byte value's
            primary purpose is priority resolution, and the bytes are equally weighted in §7.11. Audit:
            consider whether to split the enum in a follow-up; the .pann byte budget is ample.
        - Increment "edges propagated" counter.

4. Once edges queue empty, walk every recording in scope's TrackSections in trajectory-time order to seal End ε
   on the segment that owns the dock event UT — needed for the §6.4 "Both" lerp case where a long burn ends at
   a dock. The End ε is the same value as the Start ε of the merged-result segment (recorded relative offset is
   zero across the dock event UT pair when the recorder ran sample-time alignment per Phase 9; for pre-Phase-9
   recordings the offset is at most a few ticks of sampling noise — same fidelity as Phase 2 today).

5. Emit Pipeline-AnchorPropagate Info "DAG walk summary" line.
```

### 3.3 Per-source ε resolution at session entry

For each commit-time candidate the propagator translates `(UT, Source, Side)` into ε:

| Source              | ε formula at session entry                                                                                                                                                                                                                                              |
|---------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `DockOrMerge`       | If propagated from upstream: §3.2 step 3d. If a leaf Dock that has no upstream ε (e.g., docking with a live persistent vessel that is not an ancestor): ε = `live_persistent_vessel_world_pos − P_smoothed(boundaryUT)`. The live read is HR-15 single-shot.            |
| `Undock/EVA/JointBreak` (mapped to `DockOrMerge` byte for now) | Always propagated from the parent's End ε. Pure §9.1 rule.                                                                                                                                                                                                              |
| `RelativeBoundary`  | ε = `P_relativeResolved(boundaryUT) − P_smoothed_ABSOLUTE(boundaryUT)`. See §6 below for the resolver dispatch (no new math — calls existing `TrajectoryMath.ResolveRelativePlaybackPosition` or v7 absolute shadow when present).                                       |
| `OrbitalCheckpoint` | ε = `KeplerEval(orbitSegment, boundaryUT) − P_smoothed_ABSOLUTE(boundaryUT)`. KeplerEval is the existing analytical resolver on `OrbitSegment` (already used by `ParsekFlight.TryInterpolateAndPositionCheckpointSection`).                                              |
| `SoiTransition`     | Same as `OrbitalCheckpoint` but with the post-SOI `OrbitSegment` for the post-side and the pre-SOI for the pre-side.                                                                                                                                                    |
| `Loop`              | ε = `(loopAnchorVessel.GetWorldPos3D() + recordedLoopOffset(loopPhaseAtUT)) − P_smoothed(UT)`. Anchor vessel resolved via `Recording.LoopAnchorVesselPersistentId`. HR-15: read once at session entry.                                                                  |
| `SurfaceContinuous` | ε = `Vector3d.zero`. Phase 6 marker only — Phase 7 will compute ε per frame from terrain raycast.                                                                                                                                                                       |
| `BubbleEntry`/`BubbleExit` | Same shape as `OrbitalCheckpoint` but the reference is the boundary sample of the high-fidelity (`Active`/`Background`) side of the section-source transition.                                                                                                          |

`P_smoothed(UT)` is evaluated through `SectionAnnotationStore.TryGetSmoothingSpline` + `TrajectoryMath.CatmullRomFit.Evaluate` exactly as `RenderSessionState.RebuildFromMarker` already does. The body-fixed vs inertial dispatch follows the existing `FrameTag != 0` skip-logic from `RenderSessionState.cs:699-722`.

### 3.4 Cross-recording propagation (§9.3)

The walk needs to cross recording boundaries at chain tips (e.g., R_A's last segment docks into R_B's continuation). Two mechanisms exist in the codebase:

1. `RecordingTree.BranchPoints` — every `Dock` BranchPoint already lists `ParentRecordingIds` + `ChildRecordingIds`, often spanning recordings within the same tree. The walk in §3.2 step 2 already enumerates these.
2. **Chain links across trees:** `Recording.ChainId` + `Recording.ParentRecordingId` (the per-recording chain identity) plus `Recording.ChainIndex`. The propagator additionally walks chain edges by treating consecutive chain segments as a single propagation edge whose `recordedOffsetAtEvent` is zero (chain continuation is the same vessel: PID continuity).

For Phase 6, both mechanisms are exercised by the same edge-queue. The propagator builds a unified edge set:

```
foreach tree in trees in scope:
    foreach bp in tree.BranchPoints:
        foreach parent in bp.ParentRecordingIds:
            foreach child in bp.ChildRecordingIds:
                if (parent != child) edges.Add(new Edge(parent, child, bp));
foreach rec in recordings in scope where rec.ParentRecordingId != null:
    edges.Add(new Edge(parent=rec.ParentRecordingId, child=rec.RecordingId, bp=virtualChainEdge(rec)));
```

The "virtual chain edge" carries `bp.UT = parent.EndUT == child.StartUT` and zero recorded offset (PID continuity).

### 3.5 Suppressed subtree filtering (§9.4 / HR-8)

Already shipped: `SessionSuppressionState.IsSuppressed(string recordingId)` returns the closure for the active marker. The propagator queries it for both endpoints of every edge:

- If the **child** is suppressed, the edge is skipped and the child's downstream is not walked. Emit `Pipeline-AnchorPropagate Verbose "suppressed-predecessor"` (per §19.2 Stage 3b row 3).
- If the **parent** is suppressed but the child is not, the child cannot inherit ε from a suppressed parent (HR-8). The child falls back to whatever non-suppressed reference is available — typically zero ε (§9.4). Log the same "suppressed-predecessor" line so the diagnostic surface is symmetric.

### 3.6 Cycle-free by construction (§9.5)

The DAG is acyclic by construction (HR-13). The walk additionally maintains a `HashSet<AnchorKey> visited` indexed by the same key as `RenderSessionState.Anchors`. A second-time arrival at the same key is a cycle (programmer error) — log a `Pipeline-AnchorPropagate Warn` line ("cycle suspected, halting subtree") and stop. Defense-in-depth per HR-13.

### 3.7 Termination conditions (§9.5)

The walk terminates naturally when:

- The queue is empty.
- A segment has no downstream successors in the DAG (chain terminates / `Terminal` BranchPoint).
- The successor is suppressed (§3.5).
- The recording ends (`rec.EndUT` reached without a downstream BranchPoint).

Each termination case increments a counter that lands in the §19.2 "DAG walk summary" Info line.

### 3.8 ERS-exempt comment

`AnchorPropagator.cs` reads `RecordingStore.CommittedRecordings` and `RecordingStore.CommittedTrees`. Per `.claude/CLAUDE.md` ERS routing, every such read needs `[ERS-exempt]` + an allowlist entry. The rationale: same as `RenderSessionState.cs:1-6` — the marker's `OriginChildRecordingId` resolves to a `NotCommitted` provisional that ERS would filter out by construction. Add a file-level comment on `AnchorPropagator.cs` and append `Source/Parsek/Rendering/AnchorPropagator.cs` to `scripts/ers-els-audit-allowlist.txt` with the same one-line rationale.

---

## 4. Priority Resolver (§7.11)

The priority order from §7.11 expressed as a comparator over `AnchorSource`:

```
private static readonly int[] AnchorPriorityRank = new int[10] {
    /* 0 LiveSeparation     */ 1,   // §7.1, top priority (live reference)
    /* 1 DockOrMerge        */ 4,   // §7.2/7.3, DAG-propagated rule 4
    /* 2 RelativeBoundary   */ 2,   // §7.4, real persistent-vessel reference
    /* 3 OrbitalCheckpoint  */ 3,   // §7.5, analytical-orbit reference
    /* 4 SoiTransition      */ 3,   // §7.6, analytical-orbit reference
    /* 5 BubbleEntry        */ 6,   // §7.7
    /* 6 BubbleExit         */ 6,   // §7.7
    /* 7 CoBubblePeer       */ 5,   // §7.8 (Phase 5)
    /* 8 SurfaceContinuous  */ 2,   // §7.9 — equivalent to RelativeBoundary (real persistent reference)
    /* 9 Loop               */ 2,   // §7.10 — equivalent to RelativeBoundary
};
```

Lower rank wins. Ties are broken deterministically by enum value (HR-3).

### 4.1 Where the resolver lives

**Inline at the candidate emission site, not as a separate static helper.** Two places call it:

1. **`AnchorCandidateBuilder.Compute`** — when two producers emit candidates at the same `(recordingId, sectionIndex, UT, Side)`, the resolver picks the winner before the `.pann` write. This collapses redundant candidates at commit time (saves bytes on disk).
2. **`AnchorPropagator.Run`** — when the propagator computes ε for a session-time slot already occupied (e.g., LiveSeparation winning over DAG-propagated DockOrMerge per §7.11), the resolver compares the existing entry's `Source` against the candidate's `Source` and only overwrites if the new one ranks lower.

A small helper does the comparison:

```csharp
internal static class AnchorPriority
{
    private static readonly int[] Rank = { ... as above };

    internal static int RankOf(AnchorSource source) => Rank[(int)source];

    /// <summary>Returns true when 'candidate' should replace 'existing' at the same slot.</summary>
    internal static bool ShouldReplace(AnchorSource existing, AnchorSource candidate)
        => RankOf(candidate) < RankOf(existing)
        || (RankOf(candidate) == RankOf(existing) && (int)candidate < (int)existing);
}
```

The current `RenderSessionState.Anchors` map is keyed `(recordingId, sectionIndex, AnchorSide)` — only one ε per slot. The propagator inserts via:

```csharp
var key = new AnchorKey(...);
if (Anchors.TryGetValue(key, out AnchorCorrection existing) &&
    !AnchorPriority.ShouldReplace(existing.Source, candidate.Source)) {
    // existing wins, log "Anchor source priority resolution" Verbose line per §19.2 Stage 3 row 3
    return;
}
Anchors[key] = candidate;
```

This is the §19.2 Stage 3 "Anchor source priority resolution" Verbose line — emitted whenever multiple candidates contend for the same slot, naming each contender + winner.

---

## 5. .pann Schema Verification + AlgorithmStampVersion Bump

### 5.1 Current state

- `PannotationsBinaryVersion = 1` (`PannotationsSidecarBinary.cs:97`). **Phase 6 keeps this at 1.**
- `AlgorithmStampVersion = 2` (`PannotationsSidecarBinary.cs:103`, bumped from 1 to 2 in Phase 4 for inertial-longitude splines).
- `AnchorCandidatesList` block: schema is in §17.3.1 and reserved at the byte layout — current code writes `count = 0` and the reader rejects any non-zero count (`PannotationsSidecarBinary.cs:305-313`).

### 5.2 Phase 6 schema-shape additions

The §17.3.1 schema for `AnchorCandidatesList` is:

```
[..]      AnchorCandidatesList
            count : int32
            entries[count] :
              sectionIndex : int32
              utCount      : int32
              uts          : double[utCount]
              types        : byte[utCount]
```

Phase 6 packs `AnchorSide` into bit 7 of `types[i]` (§2.4 above). No change to schema layout. The `Side` bit is a forward-compatible extension because every existing reader simply masks the byte — a pre-Phase-6 reader interpreting a Phase-6 file would see invalid `AnchorSource` values (e.g., `0x82`) and reject. That rejection is acceptable: the `.pann` is regenerable, and the `AlgorithmStampVersion` bump ensures pre-Phase-6 binaries discard such files anyway.

### 5.3 AlgorithmStampVersion bump

**Bump `AlgorithmStampVersion = 2 → 3`.** Rationale (HR-10):

- The `AnchorCandidatesList` block transitions from "always empty" to "populated by `AnchorCandidateBuilder`". Any existing `.pann` file with `AlgorithmStampVersion = 2` is missing the candidates block and would force a runtime fallback for every consumer. The bump triggers the existing `ClassifyDrift → "alg-stamp-drift"` path in `SmoothingPipeline.LoadOrCompute`, which discards the stale `.pann` and recomputes (splines + candidates).
- Document the bump in a new code comment on the `AlgorithmStampVersion` field in `PannotationsSidecarBinary.cs:97-103` (the existing comment already chronicles the Phase 4 v1→v2 bump; append the v2→v3 entry).

### 5.4 Configuration hash

`SmoothingConfiguration.Default` is unchanged. The §17.3.1 `ConfigurationHash` covers tunables; Phase 6 introduces no new tunable (anchor priority is a hard-coded constant table, deliberately not a tunable — that would invite reordering by users and silently change ε-winner outcomes across saves). Document this decision in `AnchorPriority` docstring: "Order is fixed per design doc §7.11 and is not a tunable. If the design ever needs runtime configurability, add it to the `.pann` ConfigurationHash and bump `AlgorithmStampVersion`."

---

## 6. RELATIVE-Segment Boundary Anchor (§7.4) Special Handling

Per the design doc §7.4 and `.claude/CLAUDE.md` "Rotation / world frame": **RELATIVE-frame positions are already exact via `TrajectoryMath.ResolveRelativePlaybackPosition`**. Phase 6 must not re-implement the v5 / v6 / v7 resolver math.

**The §7.4 candidate emits on the ABSOLUTE side of the boundary, not the RELATIVE side.** Why:

- The RELATIVE side is exact at every UT — no ε needed.
- The ABSOLUTE side is the side that has accumulated frame drift / smoothing residual. The boundary value (computed via the version-appropriate RELATIVE resolver) is what the ABSOLUTE side should match at its endpoint.

Per §3.3 above:

```
ε_at_absolute_boundary = P_relativeResolved(boundaryUT) − P_smoothed_ABSOLUTE(boundaryUT)
```

`P_relativeResolved(boundaryUT)` dispatch:

1. **If recording format ≥ v7 AND `section.absoluteFrames` is non-empty for the RELATIVE side AND the marker's `ActiveReFlyRecordingId` matches the section's `anchorVesselId`:** use `ParsekFlight.ResolveAbsoluteShadowPlaybackFrames` — the v7 absolute shadow is already the recorder's snapshot of the focused vessel's true world position at the boundary sample. This is the highest-fidelity path.
2. **Else if recording format ≥ v6:** use `TrajectoryMath.ApplyRelativeLocalOffset(anchorWorldPos, anchorWorldRotation, dx, dy, dz)` — anchor-local Cartesian offset.
3. **Else (v5 and earlier):** use the legacy `ApplyRelativeOffset` path via `TrajectoryMath.ResolveRelativePlaybackPosition(..., recordingFormatVersion)`.

The dispatch is a **single call** to `TrajectoryMath.ResolveRelativePlaybackPosition` (it already routes by `recordingFormatVersion`); the v7 shadow is consulted via a sibling helper that the propagator wraps in a small static method `TryResolveRelativeBoundaryWorldPos`:

```csharp
private static bool TryResolveRelativeBoundaryWorldPos(
    Recording rec, TrackSection relativeSection, double boundaryUT,
    Func<string, double, double, double, Vector3d> surfaceLookup,
    out Vector3d worldPos)
{
    // 1. v7 absolute-shadow path:
    if (rec.RecordingFormatVersion >= RecordingStore.RelativeAbsoluteShadowFormatVersion
        && relativeSection.absoluteFrames != null && relativeSection.absoluteFrames.Count > 0)
    {
        // Find the absoluteFrames sample closest to boundaryUT (exact for sample-aligned boundaries).
        TrajectoryPoint p = FindBoundaryShadowSample(relativeSection.absoluteFrames, boundaryUT);
        if (TryLookupSurfacePosition(surfaceLookup, p.bodyName, p.latitude, p.longitude, p.altitude, out worldPos))
            return true;
    }

    // 2. v6 anchor-local Cartesian or v5 legacy world-offset path.
    //    Anchor world pose at boundaryUT comes from RecordingStore.CommittedRecordings
    //    by looking up rec.AnchorVesselId on the section.  HR-15 single-read at session entry.
    Vector3d anchorWorld; Quaternion anchorRot;
    if (!TryReadAnchorPoseAt(rec, relativeSection.anchorVesselId, boundaryUT, out anchorWorld, out anchorRot))
    {
        worldPos = default; return false;
    }
    TrajectoryPoint pt = FindBoundaryShadowSample(relativeSection.frames, boundaryUT);
    worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
        anchorWorld, anchorRot, pt.latitude, pt.longitude, pt.altitude,
        rec.RecordingFormatVersion);
    return true;
}
```

Phase 6 introduces zero new resolver math here — every world-frame call funnels through one of `ResolveRelativePlaybackPosition`, `ApplyRelativeLocalOffset`, or `body.GetWorldSurfacePosition`. Document in the file header comment: "RELATIVE boundary anchor adds no new resolver math; it composes existing helpers."

If neither path resolves (no absolute shadow, no anchor pose, no boundary sample), emit `Pipeline-Anchor Warn "relative-boundary-resolve-failed"` (HR-9), set ε to `Vector3d.zero`, and continue.

---

## 7. Logging Contract (§19.2 Stage 3 + Stage 3b)

Every new Pipeline-Anchor and Pipeline-AnchorPropagate line below honours `.claude/CLAUDE.md` "Logging Requirements" and the §19.2 contract. Format is `[Parsek][LEVEL][Pipeline-<Stage>] <message>`. All Verbose lines that could fire per-frame are dedup'd via `RenderSessionState.NotifyXxx`-style helpers (mirror the Phase 3 lerp dedup pattern).

### 7.1 Pipeline-Anchor (Stage 3)

| Event                                              | Level   | Tag             | Data + dedup                                                                                                                |
|----------------------------------------------------|---------|-----------------|-----------------------------------------------------------------------------------------------------------------------------|
| Anchor candidate computed at commit                | Verbose | Pipeline-Anchor | `recordingId, sectionIndex, candidateUT, candidateType, side`. Per-section summary preferred; one line per emit if Verbose. Dedup: per-recording per-section per-source (one line per `(recordingId, sectionIndex, source)`). |
| Anchor candidate summary at commit                 | Verbose | Pipeline-Anchor | `recordingId, sections=N, candidatesEmittedTotal=M, perSourceCounts=[Dock=A, Undock=B, ...]`. One line per `BuildAndStorePerSection` call. |
| Anchor ε computed at session entry (per source)    | Info    | Pipeline-Anchor | `recordingId, sectionIndex, side=Start/End, source, epsilonMagnitudeM, splineHit, anchorUT`. Mirrors Phase 2's existing line shape. |
| Anchor source priority resolution                  | Verbose | Pipeline-Anchor | `recordingId, sectionIndex, side, ut, candidates=[Source1@rank1, Source2@rank2, ...], winner=Source1`. Fires only when ≥ 2 candidates contend. Dedup: per-session per `(recordingId, sectionIndex, side)`. |
| Anchor missing → constant ε = 0                    | Verbose | Pipeline-Anchor | `recordingId, sectionIndex, reason="no-upstream-anchor / suppressed-parent / no-candidate"`. Dedup: per-session per slot.    |
| Live anchor read at frozen UT                      | Verbose | Pipeline-Anchor | (already shipped in Phase 2; unchanged.)                                                                                   |
| Anchor ε exceeds bubble radius (sanity)            | Warn    | Pipeline-Anchor | (already shipped in Phase 2; unchanged. HR-9.)                                                                              |
| RELATIVE boundary resolve failed                   | Warn    | Pipeline-Anchor | `recordingId, sectionIndex, boundaryUT, reason="no-shadow / no-anchor-pose / no-sample"`. HR-9.                            |

### 7.2 Pipeline-AnchorPropagate (Stage 3b)

| Event                                              | Level   | Tag                         | Data + dedup                                                                                          |
|----------------------------------------------------|---------|-----------------------------|--------------------------------------------------------------------------------------------------------|
| DAG walk start                                     | Info    | Pipeline-AnchorPropagate    | `rootRecordingId, treeId, sessionId`. Once per `Run()`.                                                |
| Edge propagated                                    | Verbose | Pipeline-AnchorPropagate    | `parentRecordingId→childRecordingId, branchPointType, bpUT, epsilonDeltaM`. Dedup: per-edge.           |
| Suppressed predecessor skipped                     | Verbose | Pipeline-AnchorPropagate    | `parentRecordingId, childRecordingId, reason="suppressed-parent" or "suppressed-child"`. Dedup: per-edge. |
| Cross-recording chain edge                         | Verbose | Pipeline-AnchorPropagate    | `parentRecordingId→childRecordingId, chainEdge=true, bpUT, epsilonDeltaM`. Dedup: per-edge.            |
| Cycle suspected (HR-13 defense)                    | Warn    | Pipeline-AnchorPropagate    | `recordingId, sectionIndex, side`. Halts the subtree. Dedup: per-session per key.                      |
| DAG walk summary                                   | Info    | Pipeline-AnchorPropagate    | `edgesVisited=N, anchorsSet=M, terminationsBySource={no-successor=A, suppressed=B, recording-end=C}, durationMs`. Once per `Run()`. |

### 7.3 Dedup strategy

Mirror Phase 3's pattern in `RenderSessionState.cs:919-1007` — per-session `HashSet<AnchorKey>` keyed by `(recordingId, sectionIndex, side)`, cleared on `Clear` / `RebuildFromMarker` / `ResetForTesting`. Add four new sets to `RenderSessionState`:

```csharp
private static readonly HashSet<AnchorKey> AnchorCandidateLogged = new HashSet<AnchorKey>();
private static readonly HashSet<AnchorKey> PriorityResolutionLogged = new HashSet<AnchorKey>();
private static readonly HashSet<string> EdgePropagatedLogged = new HashSet<string>();   // key: "parent→child:bpUT"
private static readonly HashSet<string> SuppressedPredecessorLogged = new HashSet<string>();
```

`ResetSessionDedupSetsLocked()` clears all of them alongside the existing four.

### 7.4 LogContractTests entries

`Source/Parsek/InGameTests/LogContractTests.cs` already pins format for every existing `Pipeline-*` Warn. Add contract entries for the new Warn lines:

- `Pipeline-Anchor Warn "relative-boundary-resolve-failed"`
- `Pipeline-AnchorPropagate Warn "cycle suspected"`

---

## 8. Settings Flag Decision

**Recommendation: introduce a new flag `useAnchorTaxonomy` (default `true`), separate from `useAnchorCorrection`.**

Justification:

- `useAnchorCorrection` (Phase 2) gates the **render-time consumption** of any anchor in the `RenderSessionState` map (the hook in `ParsekFlight.allowAnchorCorrectionInterval`). It controls whether ε is added to the smoothed position at render time.
- `useAnchorTaxonomy` (Phase 6) gates the **commit-time emission of candidates** (`AnchorCandidateBuilder.BuildAndStorePerSection`) AND the **session-time DAG walk** (`AnchorPropagator.Run`). It controls whether non-LiveSeparation entries are populated into the map at all.

Two flags decouple two distinct concerns:

1. A user can turn `useAnchorTaxonomy` off to get **only** Phase 2 LiveSeparation behaviour while keeping the Phase 2 + 3 lerp path active for that single anchor type — a useful regression-bisection tool.
2. A user can turn `useAnchorCorrection` off entirely, suppressing every ε path including LiveSeparation — the current Phase 2 escape hatch.

Reusing `useAnchorCorrection` would force the Phase 6 rollout to land all-or-nothing alongside Phase 2's existing behaviour. Two flags also match the Phase 1 (`useSmoothingSplines`) precedent.

### 8.1 Flag plumbing (mirrors `useAnchorCorrection`)

`Source/Parsek/ParsekSettings.cs`:

```csharp
[GameParameters.CustomParameterUI("Use anchor taxonomy",
    toolTip = "When on (Phase 6), every anchor type from §7.1–§7.10 produces an AnchorCorrection and DAG propagation walks BranchPoint edges.")]
public bool useAnchorTaxonomy
{
    get { return _useAnchorTaxonomy; }
    set
    {
        if (_useAnchorTaxonomy == value) return;
        bool prev = _useAnchorTaxonomy;
        _useAnchorTaxonomy = value;
        NotifyUseAnchorTaxonomyChanged(prev, value);
        if (ParsekSettingsPersistence.IsReconciled)
            ParsekSettingsPersistence.RecordUseAnchorTaxonomy(value);
    }
}
private bool _useAnchorTaxonomy = true;

internal static void NotifyUseAnchorTaxonomyChanged(bool oldValue, bool newValue)
{
    if (oldValue == newValue) return;
    ParsekLog.Info("Pipeline-Anchor", $"useAnchorTaxonomy: {oldValue}->{newValue}");
}
```

`Source/Parsek/ParsekSettingsPersistence.cs`: add `UseAnchorTaxonomyKey`, `storedUseAnchorTaxonomy`, `RecordUseAnchorTaxonomy`, plus the same restoration block that exists for `useAnchorCorrection`.

`AnchorCandidateBuilder.BuildAndStorePerSection` and `AnchorPropagator.Run` early-out (with a `Pipeline-Anchor Verbose "useAnchorTaxonomy=false, skipping"` line, dedup'd per-process) when the flag is off.

LiveSeparation continues to gate on `useAnchorCorrection` only. The two flags compose: with both on → full Phase 6 behaviour; with `useAnchorTaxonomy` off and `useAnchorCorrection` on → Phase 2 behaviour (LiveSeparation only); with `useAnchorCorrection` off → no ε at all.

---

## 9. Test Plan

Follows §20.1 / §20.2 / §20.3 / §20.5 conventions — every test has a "what makes it fail" justification.

### 9.1 xUnit unit tests

**File: `Source/Parsek.Tests/Rendering/AnchorPropagationTests.cs`** (new, `[Collection("Sequential")]`)

- `Propagate_AppliesNinePointOneFormula`: feeds `Propagate(εUpstream, recordedOffset, smoothedOffset)` known vectors; asserts `ε' = εUpstream + (recordedOffset − smoothedOffset)`. **Fails when:** the formula is mistyped (sign flip, wrong order). Three-stage rocket §9.2 spec.
- `ThreeStageRocket_PropagatesLiveAnchorToL2U_AndOnToU`: builds an `RecordingTree` fixture with L1 (live), L2+U (ghost), L2 (ghost), U (ghost) and two BranchPoints (L1/L2+U separation; L2/U separation). Seeds the LiveSeparation anchor via `RebuildFromMarker`, then runs `AnchorPropagator.Run`. Asserts `ε_{L2+U,start}`, `ε_{L2,start}`, `ε_{U,start}` are populated and match the expected propagation formula. **Fails when:** propagation skips an edge or applies the wrong recorded offset.
- `CrossRecording_DockAtChainTip_PropagatesEpsilonAcrossRecordingBoundary`: two recordings R_A (chain tip) and R_B (continuation post-dock); BranchPoint of type `Dock` lists R_A in `ParentRecordingIds` and R_B's first segment in `ChildRecordingIds`. Asserts R_B's start ε is derived from R_A's end ε. **Fails when:** propagator does not enumerate cross-recording edges.
- `SuppressedPredecessor_DoesNotLeakEpsilonIntoSuccessor`: builds a tree with parent rec P (suppressed via `SessionSuppressionState`) and child C (not suppressed). Runs propagator; asserts C's start ε is **not** populated from P (HR-8). Asserts the `Pipeline-AnchorPropagate Verbose "suppressed-predecessor"` line is emitted. **Fails when:** suppression filter is bypassed (HR-8 violation).
- `Cycle_HaltsSubtreeAndWarns`: builds a malformed tree that creates a fake cycle (manual edge insertion bypassing the DAG invariant). Runs propagator; asserts the walk halts at the cycle and emits the `Pipeline-AnchorPropagate Warn "cycle suspected"` line. **Fails when:** the visited-set check is removed or broken.

**File: `Source/Parsek.Tests/Rendering/AnchorCandidateBuilderTests.cs`** (new)

- `EmitsDockMergeCandidate_AtBranchPointUT`: BranchPoint of type `Dock` at UT t_m. Both sides get a candidate at t_m. **Fails when:** candidate UT is rounded or shifted.
- `EmitsRelativeBoundaryCandidate_OnAbsoluteSideOnly`: TrackSection list with `Absolute` then `Relative`. Asserts a candidate is emitted on the ABSOLUTE-section's `End` slot, never on the RELATIVE side. Reverse order test (RELATIVE→ABSOLUTE) emits on the ABSOLUTE-section's `Start` slot. **Fails when:** §7.4 boundary side is wrong or §7.4 emits on the RELATIVE side.
- `EmitsOrbitalCheckpointCandidate_AtBoundary`: ABSOLUTE → OrbitalCheckpoint (Kepler) → ABSOLUTE. Asserts candidates on both ABSOLUTE sides at the boundaries. **Fails when:** §7.5 misses one side.
- `EmitsSoiTransitionCandidate_OnBodyChangeAtCheckpoint`: two `OrbitSegment`s with different `bodyName` adjacent at a checkpoint section. Asserts `SoiTransition` source on **both** sides. **Fails when:** SOI is collapsed into plain OrbitalCheckpoint or only one side gets a candidate.
- `EmitsSurfaceContinuousMarker_OnSurfaceMobileSection`: `SegmentEnvironment.SurfaceMobile` section. Asserts one Start-side candidate per such section. **Fails when:** §7.9 is silently elided.
- `EmitsLoopMarker_OnRecordingWithLoopAnchorPid`: `Recording.LoopIntervalSeconds = 60` and `LoopAnchorVesselPersistentId = 1234`. Asserts a Loop candidate on the first eligible section. **Fails when:** §7.10 marker absent.
- `Phase6CandidatesAreSorted_PerSectionByUT`: builds a recording with multiple BranchPoints inside one section in non-monotonic order. Asserts the per-section candidate array is UT-monotonic after `Compute`. **Fails when:** the §7.11 winner-collapse step also changes ordering.

**File: `Source/Parsek.Tests/Rendering/AnchorPriorityTests.cs`** (new)

- `PriorityRank_MatchesSeven11Order`: hard-codes the §7.11 priority ordering and asserts `AnchorPriority.RankOf` returns ranks consistent with it for all 10 enum values. **Fails when:** any rank value drifts.
- `ShouldReplace_HigherPriorityWins`: pairs `LiveSeparation` vs `DockOrMerge` → LiveSeparation wins. `RelativeBoundary` vs `OrbitalCheckpoint` → RelativeBoundary wins (rank 2 < rank 3). `BubbleEntry` vs `CoBubblePeer` → CoBubblePeer wins (rank 5 < rank 6). **Fails when:** the rank table or comparator is wrong.
- `ShouldReplace_TieBreakerByEnumValue`: two same-rank sources (e.g., `RelativeBoundary` rank 2 vs `Loop` rank 2) → enum value tiebreak (RelativeBoundary's enum value `2` < Loop's `9` → RelativeBoundary wins). **Fails when:** ties are non-deterministic (HR-3).

**File: `Source/Parsek.Tests/Rendering/PannotationsSidecarRoundTripTests.cs`** (extend existing)

- `AnchorCandidatesList_RoundTripsByteIdenticalAcrossWriteRead`: writes a candidate set with mixed sources and sides, reads it back, asserts every (sectionIndex, UT, source, side) pairs match. Includes one entry with side=End (high bit set) to exercise the bit-pack. **Fails when:** the bit-pack collides with valid source values or `AnchorSide` is dropped.
- `AnchorCandidatesList_DiscardsOnAlgorithmStampVersionMismatch`: writes a `.pann` with `AlgorithmStampVersion = 2` (Phase 4 stamp); reads under Phase 6 code (`expected = 3`); asserts `ClassifyDrift` returns `"alg-stamp-drift"` and the file is discarded. **Fails when:** stale candidates from a prior algorithm version leak into Phase 6 (HR-10).
- `AnchorCandidatesList_OverflowGuardsRejectInsaneCount`: crafted file with `count = int.MaxValue` for the candidate block; asserts `ValidateCount` rejects with the cap-exceeded reason. **Fails when:** the cap or stream-length check is removed.

**File: `Source/Parsek.Tests/Rendering/RenderSessionStateTests.cs`** (extend existing)

- `RebuildFromMarker_RunsAnchorPropagatorAfterLiveSeparation`: uses the test overload (the existing seam) plus an injected `treeLookup` and `recordings` list with one Dock BranchPoint. Asserts the propagator is invoked and a non-LiveSeparation anchor lands in the map. **Fails when:** the Phase 6 hook is missed or runs before the Phase 2 anchor write.
- `RebuildFromMarker_PriorityResolutionEmitsLogLine`: synthetic case where two candidates contend at the same slot. Asserts the `Pipeline-Anchor Verbose "Anchor source priority resolution"` line is emitted with both candidates and the winner. **Fails when:** the log line is dropped (§19.2 Stage 3 row 3).

### 9.2 Log-assertion tests

**File: `Source/Parsek.Tests/Rendering/AnchorPropagationLoggingTests.cs`** (new)

Pattern follows `RenderSessionStateLoggingTests.cs`:

- DAG walk start + end Info pair fires exactly once per `Run()`.
- Edge propagated Verbose fires once per edge (dedup verified by repeating `Run()` and asserting no double-emit).
- Suppressed-predecessor Verbose fires for the suppressed edge.
- Cycle Warn fires on the synthetic cycle fixture.

**File: `Source/Parsek.Tests/Rendering/AnchorCandidateLoggingTests.cs`** (new)

- Per-section commit-time summary line fires once per `BuildAndStorePerSection`.
- Priority-resolution Verbose fires when `Compute` collapses two candidates at the same slot.

### 9.3 In-game tests (`InGameTests/RuntimeTests.cs`)

Per §20.5 Phase 6 row, the canonical in-game test is `Pipeline-DAG-Three-Stage`. Phase 6 also wants every §7.1–§7.10 anchor type exercised at least once. Many already exist:

- `Pipeline_Anchor_LiveSeparation` — exists (§7.1).
- `Pipeline_Smoothing_NoJitterOnCoast` — exists.

Phase 6 adds:

- `Pipeline_DAG_Three_Stage` (`[InGameTest(Category = "Pipeline-AnchorPropagate", Scene = GameScenes.FLIGHT)]`) — synthetic three-stage rocket recording fixture; spawns L1 live, asserts L2+U, L2, U all spawn at expected world positions per the §9.2 propagation formula. Tolerance: ε per stage < 0.5 m. **Fails when:** propagator drops an edge or applies the wrong sign in §9.1.
- `Pipeline_Anchor_Dock` (Category `Pipeline-Anchor`) — recording with a mid-recording dock event; asserts the merged-result segment's start ε matches the recorded relative offset between the docking pair.
- `Pipeline_Anchor_RelativeBoundary` — RELATIVE→ABSOLUTE seam fixture (already in §20.3 list); assert no visible snap at the boundary. Phase 6 wires the candidate emission; the test verifies the anchor lands on the ABSOLUTE side.
- `Pipeline_Anchor_OrbitalCheckpoint` — burn-end / coast-start fixture; assert ε bracketed by checkpoint-derived position.
- `Pipeline_Anchor_SOI` — Kerbin → Mun crossing; assert correct body for each side and non-zero ε on the post-side.
- `Pipeline_Anchor_BubbleEntry` — recording with mid-record bubble exit / re-entry; assert candidate emitted at re-entry sample.
- `Pipeline_Anchor_Loop` — looped rover near a live vessel; assert no drift across cycles (matches the §20.3 `Pipeline-Loop-Anchor` test).
- `Pipeline_Anchor_SuppressedSubtree` — runs the §9.4 suppressed-predecessor scenario in-game (after a Rewind-to-Staging session); asserts the suppressed-side ghost's ε is not propagated downstream.

Each test logs one summary line in the existing `Pipeline_Anchor_LiveSeparation` style for the in-game results pane.

### 9.4 .pann round-trip tests

Already covered by `PannotationsSidecarRoundTripTests.cs` (the new entries in §9.1). Discard-on-`AlgorithmStampVersion`-mismatch is also there.

### 9.5 Synthetic recordings

Reuse fixtures from the existing §20.4 list; specifically `pipeline-anchor-separation` (already exists for Phase 2) and add a Phase-6-specific synthetic for the three-stage rocket if a generator does not already produce one. `Source/Parsek.Tests/Generators/RecordingBuilder.cs` likely already has `WithBranchPoint(BranchPointType, ut, ...)` and a chain-builder; if not, extend it minimally. No `.prec` schema bump required.

---

## 10. CHANGELOG + todo Updates

Both files need the same commit-time edits (per `.claude/CLAUDE.md` "Documentation Updates — Per Commit, Not Per PR").

### 10.1 CHANGELOG.md

Under the existing `## 0.9.1` block, append at the end of `### Internals` (the Phase 4 entry already lives there):

```
- **Ghost trajectory rendering Phase 6: anchor taxonomy completion + DAG propagation.** Every anchor type in design-doc §7.1–§7.10 now produces an `AnchorCorrection` (Phase 2 already shipped §7.1; Phase 6 adds Dock/Merge, Undock/EVA/JointBreak, RELATIVE-boundary, OrbitalCheckpoint, SOI transition, BubbleEntry/Exit, Loop, plus a SurfaceContinuous marker that Phase 7 will resolve per-frame). DAG propagation walks `BranchPoint` edges per §9.1, including across recordings at chain tips (§9.3) and skipping suppressed predecessors (§9.4). Anchor priority is resolved per §7.11. Behind a new `useAnchorTaxonomy` settings toggle (default on). `.pann AnchorCandidatesList` block populated; `AlgorithmStampVersion` bumped to v3 so older `.pann` files are discarded and recomputed on first load (HR-10).
```

### 10.2 docs/dev/todo-and-known-bugs.md

Append a new section near the top of the active todo list:

```
## Done — v0.9.1 Phase 6 anchor taxonomy

- ~~Phase 6: emit AnchorCandidate entries for §7.2–§7.10 at commit time.~~
- ~~Phase 6: DAG propagation per §9.1 across BranchPoint edges.~~
- ~~Phase 6: cross-recording propagation at chain tips (§9.3).~~
- ~~Phase 6: suppressed-subtree filtering in propagation (§9.4 / HR-8).~~
- ~~Phase 6: §7.11 priority resolver.~~
- ~~Phase 6: `.pann AnchorCandidatesList` block populated; `AlgorithmStampVersion` v2→v3 (HR-10).~~
- ~~Phase 6: `useAnchorTaxonomy` rollout flag (default on).~~

## Phase 6 known gaps (deferred to later phases)

- §7.8 CoBubblePeer anchors are reserved in the enum but emit no candidates — Phase 5 territory.
- §7.9 SurfaceContinuous emits a marker only; the per-frame terrain raycast is Phase 7.
- §7.7 BubbleEntry/Exit candidates are emitted at session entry rather than commit time, mirroring the design doc's "physics-active state" wording. If a future format adds a per-section "ambient bubble id", revisit.
- The split anchor sources (Undock/EVA/JointBreak) currently share the `DockOrMerge` enum byte for §7.11 priority purposes. If priority order needs to differentiate them, expand the `AnchorSource` enum (one-byte budget) and bump `AlgorithmStampVersion`.
```

The Phase 6 closure entries also remove (strikethrough) any matching open items in the existing todo list — search for `Phase 6` references and update.

---

## 11. Risks / Open Questions

The Implement agent should flag these at PR time so the reviewer can weigh in.

1. **Mapping of `Undock`/`EVA`/`JointBreak` BranchPoints onto `AnchorSource.DockOrMerge`.** The enum has 10 values reserved at Phase 2; §7.3 has no dedicated byte. Phase 6 maps the three split causes to `DockOrMerge`'s priority rank (rank 4, "DAG-propagated reference"). This works for §7.11 but loses telemetry granularity in logs. If reviewers want to distinguish them in logs, add an aliased helper that synthesises the source label from the originating `BranchPointType` rather than the byte. Open: do we want to expand the enum to 11/12/13 values? That would force an `AlgorithmStampVersion` bump but is otherwise additive.

2. **Bit-7 packing for `AnchorSide` in the `.pann` types byte.** Compatible because `AnchorSource` only uses values 0..9, but a future enum expansion past 127 would collide. Open: should we instead bump `PannotationsBinaryVersion` to 2 and add a parallel `sides[]` array? The bit-pack is more storage-efficient; the parallel array is clearer. Currently going with the bit-pack on the §17.3.1 contract that "types matches AnchorSource enum".

3. **Loop anchor — when does the `LoopAnchorVesselPersistentId` get its world pose?** Phase 6 reads it once at session entry (HR-15). For loops where the anchor vessel itself is also being re-flown live, the pose at session entry may already be slightly different from the recording's anchor sample. The design doc §7.10 says the anchor is "the persistent vessel's current position"; the propagator interpretation is "the persistent vessel's current world pose at the marker invocation UT". Document the choice and surface it as a Verbose log.

4. **§7.7 BubbleEntry/Exit boundary.** The trigger wording is session-relative ("the recording session's physics bubble"), so candidates depend on which sections were physics-active under the *current* re-fly session's ambient. Phase 6 implements this as a session-time pass over `TrackSection.source` transitions (Active/Background ↔ Checkpoint). If a recording has a long Checkpoint-only section sandwiched between two Active sections, two candidates fire (Exit at the start of Checkpoint, Entry at the end). Open: is this the right interpretation, or do we want only one-side candidates per the wording?

5. **Cross-recording chain edges with PID continuity.** The propagator treats `Recording.ChainId + ParentRecordingId` as a virtual zero-offset edge. If a chain crosses a tree boundary (rare but possible in multi-tree saves), is the lookup still well-defined? `RecordingTree` topology is per-tree — a chain that crosses trees would need cross-tree resolution. Phase 6's simple list-of-recordings scan handles it correctly because the edge enumeration is over all recordings in scope, but document the assumption: chains are intra-tree by current convention.

6. **Anchor priority for §7.9 SurfaceContinuous.** Ranked at 2 (same as RelativeBoundary, real persistent reference) on the rationale that terrain is a real persistent reference. But the per-frame raycast is Phase 7 and Phase 6 emits ε = 0. A rank-2 ε of 0 will outrank an analytical OrbitalCheckpoint rank-3 ε — incorrect for any rover that overlaps an orbit checkpoint (rare). Open: should §7.9 default to rank 7 ("inactive") in Phase 6 and only promote to rank 2 once Phase 7 ships? Recommendation: yes — promote in Phase 7 with the corresponding `AlgorithmStampVersion` bump.

7. **`SmoothingPipeline.LoadOrCompute` extension.** Adding the `AnchorCandidatesList` write/read into the same call surface keeps the orchestration in one place but couples Phase 1 spline read failures to Phase 6 candidate write failures (same `try`/catch). HR-9: write failure must not abort. The existing `TryWritePann` already catches; extend it to write splines+candidates atomically. Document.

8. **Test fixture coverage for §7.4 v5/v6/v7 dispatch.** `TrajectoryMath.ResolveRelativePlaybackPosition` already routes by version. Phase 6's RELATIVE-boundary candidate must be tested for each version — if `RecordingBuilder` only produces v8 fixtures, the v5/v6 paths are untested. Open: add `WithRecordingFormatVersion(v)` to the builder if absent.

9. **Pipeline-Lerp Warn divergence threshold.** The `AnchorCorrectionInterval.DivergenceWarnThresholdM = 50.0` constant is currently never tripped because Phase 3 only ever emits Start anchors. Phase 6 unlocks End anchors → the Warn will fire frequently. Open: tune the threshold empirically before merging? Or land at 50 m, observe synthetic-recording behaviour, and tune in a follow-up.

10. **In-game test coverage for §7.7 BubbleEntry/Exit.** Constructing a synthetic recording with a mid-record bubble exit / re-entry is non-trivial — bubble entry/exit normally fires at the live vessel's physics-tick boundary. Open: is the existing test fixture infrastructure rich enough? Likely needs a `RecordingBuilder.WithSectionSourceTransition(...)` extension.

---

## 12. Step-by-Step Implement Sequence (for the Implement agent)

Each step ends in a green `dotnet test` run. Commit at every step that is independently shippable; do not bundle. No `Co-Authored-By` in any commit message. Stay in the worktree, on `feat/anchor-taxonomy-phase6`. Never edit the main `Parsek/` checkout.

### Step 1 — `AnchorCandidate` type + enum reservation audit

- Open `Source/Parsek/Rendering/AnchorCorrection.cs`. Confirm `AnchorSource` byte values 0..9 are unchanged. Add an internal struct `AnchorCandidate` (UT, Source, Side) with the bit-packing helper and constants documented.
- Extend `Source/Parsek/Rendering/SectionAnnotationStore.cs` with parallel `Dictionary<string, Dictionary<int, AnchorCandidate[]>>`, `PutAnchorCandidates`, `TryGetAnchorCandidates`, and ensure `RemoveRecording` clears both the spline and candidate maps.
- Tests: `Source/Parsek.Tests/Rendering/SectionAnnotationStoreTests.cs` extension — round-trip insert/lookup/clear.

### Step 2 — `.pann` schema population (writer + reader)

- Open `Source/Parsek/PannotationsSidecarBinary.cs`. Bump `AlgorithmStampVersion = 2 → 3` with a comment chronicling Phase 6.
- Extend `Write` signature with `IList<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates`. Replace the reserved-zero `AnchorCandidatesList` write with the real payload.
- Extend `TryRead` signature to return the candidates list. Replace the existing `if (anchorCount != 0)` rejection with a real read loop using `ValidateCount(stream, count, MaxAnchorCandidateEntries, MinBytesPerCandidateEntry, ...)`. Define `MinBytesPerCandidateEntry = 9` (sectionIndex int + utCount int + at least one UT double = 8 + 8 + 1 byte type minimum).
- Tests: extend `PannotationsSidecarRoundTripTests.cs` with the round-trip tests from §9.1.

### Step 3 — `AnchorCandidateBuilder`

- Create `Source/Parsek/Rendering/AnchorCandidateBuilder.cs`. Implement per-source helpers + `Compute` + `BuildAndStorePerSection`. Pure functions over `Recording` + `RecordingTree`. No live state reads.
- Wire `BuildAndStorePerSection` into `SmoothingPipeline.FitAndStorePerSection` (call after the spline-fit loop) and `SmoothingPipeline.LoadOrCompute` (call after spline read or after lazy-compute spline path).
- Extend `SmoothingPipeline.PersistAfterCommit` and `TryWritePann` to also persist candidates (add the candidates list to the `Write` call).
- Tests: `AnchorCandidateBuilderTests.cs` with the test list from §9.1; `SmoothingPipelineTests.cs` extension to verify candidate persistence end-to-end.
- Commit: "Phase 6 step 3: AnchorCandidateBuilder + .pann persistence".

### Step 4 — `AnchorPriority` helper

- Create the small static helper inline in `Source/Parsek/Rendering/AnchorCorrection.cs` (alongside the enum) or in a new `AnchorPriority.cs` — preference for a new file for clarity.
- Tests: `AnchorPriorityTests.cs`.
- Commit: "Phase 6 step 4: anchor priority resolver".

### Step 5 — `AnchorPropagator`

- Create `Source/Parsek/Rendering/AnchorPropagator.cs` with the `[ERS-exempt]` file-level comment and the matching `scripts/ers-els-audit-allowlist.txt` entry.
- Implement `Run(marker)` (production overload) and `Run(marker, recordings, trees, surfaceLookup)` (test overload), plus the pure `Propagate` static.
- Wire `Run(marker)` into `RenderSessionState.RebuildFromMarker` immediately after the live-separation anchor write loop.
- Wire the §7.4 RELATIVE-boundary helper `TryResolveRelativeBoundaryWorldPos` (calls `TrajectoryMath.ResolveRelativePlaybackPosition` and the v7 absolute shadow).
- Implement the suppressed-predecessor filter via `SessionSuppressionState.IsSuppressed`.
- Tests: `AnchorPropagationTests.cs` (full list from §9.1). Verify the test overload accepts a `treeLookup` lambda matching `RenderSessionStateTests.cs`'s shape.
- Commit: "Phase 6 step 5: AnchorPropagator DAG walk".

### Step 6 — Logging

- Add the new dedup sets to `RenderSessionState`, extend `ResetSessionDedupSetsLocked`, add `Notify*` methods for the new lines.
- Wire log emission inside `AnchorCandidateBuilder` (commit-time Verbose summaries), `AnchorPropagator` (DAG walk Info + Verbose lines), and `RenderSessionState` (priority resolution).
- Add `LogContractTests` entries for the new Warn lines.
- Tests: `AnchorPropagationLoggingTests.cs` + `AnchorCandidateLoggingTests.cs`.
- Commit: "Phase 6 step 6: logging contract".

### Step 7 — `useAnchorTaxonomy` flag

- Add the property + persistence + Notify per §8 (mirror `useAnchorCorrection` line-for-line).
- Wire the early-out at the head of `AnchorCandidateBuilder.BuildAndStorePerSection` and `AnchorPropagator.Run`.
- Tests: `UseAnchorTaxonomySettingTests.cs` mirroring `UseAnchorCorrectionSettingTests.cs`.
- Commit: "Phase 6 step 7: useAnchorTaxonomy rollout flag".

### Step 8 — In-game tests

- Extend `Source/Parsek/InGameTests/RuntimeTests.cs` with the new `Pipeline_DAG_Three_Stage`, `Pipeline_Anchor_Dock`, `Pipeline_Anchor_RelativeBoundary`, `Pipeline_Anchor_OrbitalCheckpoint`, `Pipeline_Anchor_SOI`, `Pipeline_Anchor_BubbleEntry`, `Pipeline_Anchor_Loop`, `Pipeline_Anchor_SuppressedSubtree` tests.
- Each test follows the existing `Pipeline_Anchor_LiveSeparation` template — synthetic recording fixtures via `RecordingBuilder`, tolerance assertion + Info log.
- Commit: "Phase 6 step 8: in-game test coverage for §7.1–§7.10".

### Step 9 — CHANGELOG + todo

- Edit `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md` per §10.
- Commit: "Phase 6: CHANGELOG + todo".

### Step 10 — Manual verification

- Run `dotnet test` (every test must pass).
- Run `dotnet test --filter InjectAllRecordings` to confirm synthetic recordings still play through the pipeline without warnings (other than the expected `Pipeline-Lerp` divergence Warns now that End anchors exist).
- Verify `KSP.log` after a representative re-fly session contains: one `Pipeline-AnchorPropagate` "DAG walk start" line, one or more "Edge propagated" Verbose lines, one "DAG walk summary" line, plus per-anchor-type `Pipeline-Anchor` Info ε lines.
- Build verification: `cd Source/Parsek && dotnet build`. Confirm the deployed DLL in `GameData/Parsek/Plugins/Parsek.dll` matches the worktree `bin/Debug/Parsek.dll` (size + mtime + grep for a distinctive new string like `"AnchorPropagator"` or `"useAnchorTaxonomy"`).
- The Phase 6 PR is ready for review.

### Critical Files for Implementation

- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/Rendering/AnchorCorrection.cs`
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/Rendering/RenderSessionState.cs`
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/Rendering/SmoothingPipeline.cs`
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/PannotationsSidecarBinary.cs`
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/BranchPoint.cs`
