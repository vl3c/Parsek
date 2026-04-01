# T97: Recording Segmentation for Selective Looping

## Goal

Make the KSP world feel alive by looping the visually interesting parts of flights (takeoffs, landings, station approaches) while skipping boring parts (long orbital coasts). Use the existing segmentation and per-segment loop toggle — no new loop mechanics, no new UI features.

---

## Existing Infrastructure

### What already works

1. **Chain splits at atmosphere boundary** — recordings auto-split into separate chain segments at atmosphere entry/exit. Each segment gets `SegmentPhase = "atmo"/"exo"/"space"` and its own `LoopPlayback` toggle.

2. **Per-recording loop control** — `Recording.LoopPlayback`, `LoopIntervalSeconds`, `LoopAnchorVesselId`. The engine remaps current UT cyclically. Each chain segment is a full Recording that loops independently.

3. **Per-segment ghost snapshots** — each chain segment captures its own `GhostVisualSnapshot` at segment start. No part-event fast-forward needed because each segment's ghost is built from its own snapshot.

4. **Per-segment part events** — each segment has its own `PartEvents` list covering only that segment's time range. No cross-segment state to reconstruct.

5. **TrackSection classification** — within each recording, trajectory data is tagged with `SegmentEnvironment` (Atmospheric, ExoPropulsive, ExoBallistic, SurfaceMobile, SurfaceStationary) and `ReferenceFrame`. This data is available even when chain splits don't happen.

6. **SOI change splits** — recordings split at SOI transitions (entering/leaving a body's sphere of influence).

### What this means

For atmospheric bodies, selective looping already works:
```
Kerbin launch → [atmo-ascent] [exo-transit] [atmo-descent] [surface]
                  ↑ loop=on                    ↑ loop=on
```
The player enables loop on the atmospheric segments, disables on the transit. Each segment is its own recording with its own snapshot. No new mechanics needed.

### Current loop flow (unchanged by this plan)

```
GhostPlaybackEngine.UpdateGhostStates
  -> for each trajectory:
     -> if ShouldLoopPlayback(traj):
        -> TryComputeLoopPlaybackUT(traj, currentUT, ...)
           -> cycleDuration = (EndUT - StartUT) + intervalSeconds
           -> loopUT = StartUT + (elapsed % cycleDuration)
```

---

## Problem 1: Airless Bodies Have No Natural Split Boundary

On Kerbin, the atmosphere edge at ~70km creates a natural chain split. On the Mun (no atmosphere), a deorbit-and-land recording is one monolithic piece. The player can't loop just the landing because it was never split out.

### Existing signals on airless bodies

| Signal | Already captured? | Where | Notes |
|--------|-------------------|-------|-------|
| ExoBallistic→ExoPropulsive | Yes | TrackSection boundary | First descent burn |
| ExoPropulsive→SurfaceMobile | Yes | TrackSection boundary | Touchdown |
| SOI entry | Yes | Chain split | Too far out (Mun SOI = 2,429 km) |
| Altitude threshold | No | Needs new detector | Analogous to atmosphere boundary |

### Solution: Altitude-based chain split for airless bodies

Add a new boundary detector parallel to `CheckAtmosphereBoundary`: **`CheckAltitudeBoundary`** for bodies without atmosphere. When altitude crosses a threshold, trigger a chain split exactly like the atmosphere boundary does.

This reuses the entire existing split infrastructure:
- `FlightRecorder` detects the crossing (with hysteresis, like atmosphere)
- `ParsekFlight.HandleAltitudeBoundarySplit` commits the segment (parallel to `HandleAtmosphereBoundarySplit`)
- `ChainSegmentManager.CommitBoundarySplit` creates the new chain segment
- New segment gets its own `GhostVisualSnapshot`, `PartEvents`, `LoopPlayback` toggle

#### Threshold formula

`approachAltitude = body.Radius * 0.15` clamped to `[5_000, 200_000]` meters.

| Body | Radius | Threshold |
|------|--------|-----------|
| Mun | 200 km | 30 km |
| Minmus | 60 km | 9 km |
| Tylo | 600 km | 90 km |
| Gilly | 13 km | 5 km (clamped) |
| Ike | 130 km | 19.5 km |
| Dres | 138 km | 20.7 km |
| Moho | 250 km | 37.5 km |
| Eeloo | 210 km | 31.5 km |

These are visually reasonable — low enough that terrain fills the screen, high enough to capture the interesting descent phase including suicide burns.

#### SegmentPhase tagging

Atmosphere splits tag segments as `"atmo"` / `"exo"`. Altitude splits on airless bodies tag as:
- `"approach"` — below the altitude threshold (descent/ascent near surface)
- `"exo"` — above the threshold (orbital operations)

#### Hysteresis

Same pattern as atmosphere boundary: require both sustained time (3s) and distance beyond boundary (1 km minimum, or a fraction of the threshold) before confirming. This prevents splits from altitude oscillation near the threshold.

#### Tree mode consideration

Current atmosphere splits are suppressed in tree mode (Bug #87) because chain splits were creating orphan segments. The same suppression would apply to altitude splits. TrackSections still record the transitions internally. If tree mode needs selective looping, that's a separate future concern (could post-process TrackSections into segments after recording ends).

---

## Problem 2: Too Many Segments in the UI

A full Kerbin-to-Mun mission might produce:
```
[Kerbin atmo ascent] [Kerbin exo] [Mun SOI exo] [Mun approach] [Mun surface]
```

That's 5 chain segments in the recordings list for what the player thinks of as "one mission." The existing `RecordingGroups` and `ChainId` grouping helps, but the segments are still individual list entries.

### Solution: Logical grouping via chain structure (no data merge)

Sequential chain segments (same `ChainId`, consecutive `ChainIndex`, no branching) are already linked. The UI can present them as a collapsible group:

```
▼ Mun Landing Mission (5 segments)              [Loop: partial]
    Kerbin atmo ascent     00:03:42   Loop ✓
    Kerbin exo             00:12:15   Loop ✗
    Mun SOI exo            00:08:30   Loop ✗
    Mun approach           00:02:10   Loop ✓
    Mun surface            00:00:45   Loop ✓
```

**No new data model needed.** The chain structure (`ChainId`, `ChainIndex`, `ChainBranch`) already provides the grouping. The only change is UI presentation:
- Group segments with same `ChainId` and `ChainBranch == 0` under a collapsible header
- Show aggregate duration and a summary loop indicator ("partial" = some segments loop)
- Bulk toggle: click the group loop indicator to enable/disable loop on all segments

This is the "super-segment" abstraction — it's a UI grouping over existing chain segments, not a new data structure. The recordings stay independent, each with their own snapshot, part events, and loop settings.

### Branching chains

When a chain branches (docking, undocking, EVA), the branch point is a natural group boundary. Each branch gets its own collapsible group. This matches the user's intuition: "merge segments up to branching/branch merge points."

```
▼ Station Resupply (3 segments, branch 0)
    Kerbin atmo ascent     Loop ✓
    Kerbin exo transit     Loop ✗
    Station approach       Loop ✓
  ├─ Branch 1: Booster recovery (2 segments)
  │   Booster descent      Loop ✓
  │   Booster splashdown   Loop ✓
```

---

## Detailed Implementation Tasks

### Phase 1: Airless body altitude split

Dependencies: none. Mirrors the existing atmosphere boundary pattern exactly.

---

**Task 1.1: Pure static methods + unit tests**

Files: `FlightRecorder.cs`, new `AltitudeSplitTests.cs`

Add two `internal static` methods to `FlightRecorder`:

```csharp
/// Returns the approach altitude threshold for an airless body.
/// body.Radius * 0.15, clamped to [5000, 200000] meters.
internal static double ComputeApproachAltitude(double bodyRadius)

/// Pure decision: should the recording split at the altitude boundary?
/// Mirrors ShouldSplitAtAtmosphereBoundary (line 3300).
/// Parameters: altitude, threshold, wasAbove, pending, pendingUT, currentUT,
///   hysteresisSeconds (default 3.0), hysteresisMeters (default max(1000, threshold*0.02))
internal static bool ShouldSplitAtAltitudeBoundary(
    double altitude, double threshold, bool wasAbove,
    bool pendingCross, double pendingUT, double currentUT,
    double hysteresisSeconds = 3.0, double hysteresisMeters = -1)
```

If `hysteresisMeters < 0`, compute as `Math.Max(1000, threshold * 0.02)`.

Guard: if `threshold <= 0`, return false (body without a computed threshold — shouldn't happen, but defensive).

Tests in `AltitudeSplitTests.cs` (mirror `AtmosphereSplitTests.cs` pattern):
- `ZeroThreshold_ReturnsFalse` — guard for degenerate input
- `SameSide_ReturnsFalse` — still above threshold, no boundary crossed
- `CrossedButNotFarEnough_ReturnsFalse` — just barely past threshold
- `CrossedFarButNotLongEnough_ReturnsFalse` — pendingCross = false
- `CrossedFarAndLongEnough_Descending_ReturnsTrue` — descending below threshold, hysteresis met
- `CrossedFarAndLongEnough_Ascending_ReturnsTrue` — ascending above threshold
- `ComputeApproachAltitude_MunRadius` — 200km → 30km
- `ComputeApproachAltitude_GillyRadius` — 13km → 5km (floor clamp)
- `ComputeApproachAltitude_TyloRadius` — 600km → 90km
- `ComputeApproachAltitude_HugeBody` — 2000km → 200km (ceiling clamp)
- `HysteresisMeters_ScalesWithThreshold` — verify default hysteresis = max(1000, threshold*0.02)

Done condition: `dotnet test --filter AltitudeSplit` passes.

---

**Task 1.2: Stateful detector in FlightRecorder**

Files: `FlightRecorder.cs`

Add instance state (parallel to atmosphere boundary fields at line 194-199):

```csharp
// Altitude boundary detection (airless bodies)
private bool wasAboveAltitudeThreshold;
private bool altitudeBoundaryPending;
private double altitudeBoundaryPendingUT;
private double currentAltitudeThreshold; // Computed once per body in ReseedAltitudeState
public bool AltitudeBoundaryCrossed { get; private set; }
public bool DescendedBelowThreshold { get; private set; } // true = entering approach zone
```

Add methods:

```csharp
/// Called every physics frame. Mirrors CheckAtmosphereBoundary (line 3329).
/// Guard: if (!IsRecording || isOnRails) return;
/// Guard: if (v.mainBody.atmosphere) return; // atmospheric bodies use atmosphere split
public void CheckAltitudeBoundary(Vessel v)

/// Reseeds altitude state. Mirrors ReseedAtmosphereState (line 3379).
/// Computes threshold, sets wasAboveAltitudeThreshold, clears pending.
private void ReseedAltitudeState(Vessel v)
```

Integration points:
- **Recording start** (line ~3824): after atmosphere state seeding, seed altitude state. Add `ReseedAltitudeState(v)` or inline equivalent.
- **`ClearBoundaryFlags`** (line 136): add `AltitudeBoundaryCrossed = false; DescendedBelowThreshold = false; altitudeBoundaryPending = false;`
- **Off-rails handler** (line ~4657, where `ReseedAtmosphereState` is called): add `ReseedAltitudeState(v)` call
- **SOI change handler** (line ~4695, where `ReseedAtmosphereState` is called): add `ReseedAltitudeState(v)` call
- **OnPhysicsFrame** (line ~4337, right after `CheckAtmosphereBoundary(v)`): add `CheckAltitudeBoundary(v)` call. NOTE: this call site is in FlightRecorder, not ParsekFlight. The detector runs in the Harmony postfix patch, same as the atmosphere check.

Done condition: builds, altitude state is seeded/cleared alongside atmosphere state.

Test: `CheckAltitudeBoundary_AtmosphericBody_Skipped` — verify the `v.mainBody.atmosphere` guard returns early on atmospheric bodies.

---

**Task 1.3: Handler in ParsekFlight**

Files: `ParsekFlight.cs`

Add `HandleAltitudeBoundarySplit` (parallel to `HandleAtmosphereBoundarySplit` at line 4187):

```csharp
/// Mirrors HandleAtmosphereBoundarySplit exactly.
/// Guards: recorder null, not recording, AltitudeBoundaryCrossed not set.
/// Tree mode suppression: if (activeTree != null) { clear flags, return; }
/// Phase tagging: completed phase = DescendedBelowThreshold ? "exo" : "approach"
///   (descending below = completing exo segment; ascending above = completing approach segment)
private void HandleAltitudeBoundarySplit()
```

NOTE: The detector call (`CheckAltitudeBoundary`) is wired in Task 1.2 (FlightRecorder.OnPhysicsFrame). This task only adds the handler that reacts to the flag.

Add `HandleAltitudeBoundarySplit()` call at line 391, right after `HandleAtmosphereBoundarySplit()`:
```csharp
HandleAtmosphereBoundarySplit();
HandleAltitudeBoundarySplit();   // NEW
HandleSoiChangeSplit();
```

Done condition: builds. Altitude boundary crossings on airless bodies trigger chain splits.

---

**Task 1.4: Phase tagging updates**

Files: `ParsekFlight.cs`, `ChainSegmentManager.cs`, `RecordingStore.cs`, `ParsekUI.cs`

1. **`TagSegmentPhaseIfMissing`** (ParsekFlight.cs line 1469): Currently tags airless bodies as `"space"`. Change to use altitude threshold:
   ```csharp
   // Was: pending.SegmentPhase = "space";
   // Now:
   double threshold = FlightRecorder.ComputeApproachAltitude(v.mainBody.Radius);
   pending.SegmentPhase = v.altitude < threshold ? "approach" : "exo";
   ```
   Apply same change at all `"space"` tagging sites in ParsekFlight.cs (lines ~1479, ~4564).

2. **`CommitVesselSwitchTermination`** (ChainSegmentManager.cs line 600): Same change — replace `"space"` with altitude-aware tagging for airless bodies.

3. **`HandleSoiChangeSplit`** (ParsekFlight.cs line ~4244): Currently tags the completed segment as `"space"` for airless departing bodies:
   ```csharp
   // Was: string fromPhase = (fromCB != null && fromCB.atmosphere) ? "exo" : "space";
   // Now: for airless bodies, use altitude threshold to distinguish approach vs exo
   ```

3. **`GetSegmentPhaseLabel`** (RecordingStore.cs line 784): No change needed — it just concatenates body name + phase string. `"Mun approach"` works.

4. **UI phase styling** (ParsekUI.cs line 1263): Add "approach" style. Currently has `phaseStyleAtmo` (orange) and `phaseStyleSpace` (lime green). Add:
   ```csharp
   private GUIStyle phaseStyleApproach; // e.g., sky blue
   // In InitStyles:
   phaseStyleApproach = new GUIStyle(GUI.skin.label);
   phaseStyleApproach.normal.textColor = new Color(0.4f, 0.7f, 1f); // sky blue
   // In phase switch:
   else if (rec.SegmentPhase == "approach") phaseStyle = phaseStyleApproach;
   ```

Done condition: builds, "approach" phase appears with distinct color in UI.

---

**Task 1.5: Integration test — synthetic Mun landing**

Files: new `AltitudeSplitIntegrationTests.cs`, possibly `Generators/RecordingBuilder.cs`

Test that simulates a Mun landing recording by:
1. Building a recording with points that descend from 100km to 0km altitude around Mun (radius 200km, no atmosphere)
2. Calling `ShouldSplitAtAltitudeBoundary` at each point to verify it fires at ~30km
3. Verifying the phase tagging: segment above 30km = "exo", segment below = "approach"

Also test:
- `ComputeApproachAltitude` for all stock airless bodies (Mun, Minmus, Tylo, Gilly, Ike, Dres, Moho, Eeloo, Bop, Pol)
- Log assertions: verify boundary detection logs match atmosphere boundary log pattern

Done condition: all tests pass.

---

### Phase 2: UI bulk loop toggle on chain headers

Dependencies: Phase 1 (so "approach" segments exist to toggle).

---

**Task 2.1: Bulk loop toggle + partial indicator**

Files: `ParsekUI.cs`

In `DrawChainBlock` (line 1730), replace the empty loop spacer (line 1782) with a functional toggle:

1. Scan chain members for loop state:
   ```csharp
   int loopOn = 0, loopOff = 0;
   for (int m = 0; m < members.Count; m++)
   {
       if (committed[members[m]].LoopPlayback) loopOn++;
       else loopOff++;
   }
   bool allLoop = loopOff == 0;
   bool noLoop = loopOn == 0;
   bool partial = !allLoop && !noLoop;
   ```

2. Draw toggle button:
   - Label: `"L"` (matches per-recording loop column)
   - Color: green if all, yellow if partial, default if none
   - Click action: if none or partial → turn all ON (encourage looping); if all on → turn all OFF
   - Log: `"Chain '{name}' bulk loop toggled: {oldState} → {newState} ({count} segments)"`

3. Replace the empty period spacer (line 1783) with aggregate info or leave as spacer.

Done condition: builds, clicking chain header L button toggles loop on all member segments.

---

### Phase 3 (optional): TrackSection altitude metadata

Dependencies: none (independent of Phase 1-2).

---

**Task 3.1: Add min/max altitude fields to TrackSection**

Files: `TrackSection.cs`, `FlightRecorder.cs`, `RecordingStore.cs`

1. Add to `TrackSection` struct:
   ```csharp
   public float minAltitude;  // NaN = not set (legacy recording)
   public float maxAltitude;  // NaN = not set
   ```
   Initialize to `float.NaN` in `StartNewTrackSection`.

2. In `FlightRecorder`, when adding a point to `currentTrackSection.frames` (lines 4442-4444, 4500-4503):
   ```csharp
   float alt = (float)point.altitude;
   if (float.IsNaN(currentTrackSection.minAltitude) || alt < currentTrackSection.minAltitude)
       currentTrackSection.minAltitude = alt;
   if (float.IsNaN(currentTrackSection.maxAltitude) || alt > currentTrackSection.maxAltitude)
       currentTrackSection.maxAltitude = alt;
   ```

3. Serialization in `RecordingStore`: add `minAlt` / `maxAlt` values to TRACK_SECTION ConfigNode. On load, default to `float.NaN` if absent (backward compat — no format version bump needed since NaN sentinel handles missing fields).

4. Update `TrackSection.ToString()` to include altitude range when non-NaN.

Test: round-trip serialization test — write TrackSection with altitude data, reload, verify values preserved. Also test loading a legacy TrackSection without altitude fields → NaN defaults.

Done condition: `dotnet test` passes, altitude metadata recorded during flight.

---

### Phase 4: Recording optimization pass

Dependencies: Phase 1 (for "approach" segments to exist). Phase 3 optional but nice for merge heuristics.

---

**Task 4.1: Pure merge/split decision logic**

Files: new `RecordingOptimizer.cs`, new `RecordingOptimizerTests.cs`

All methods `internal static` for testability:

```csharp
/// Can these two consecutive chain segments be auto-merged?
/// Returns false if any user-intent signal differs from default.
internal static bool CanAutoMerge(Recording a, Recording b)

/// Can this recording be auto-split at the given TrackSection boundary?
/// Returns false if any ghosting-trigger part events exist anywhere in the recording.
internal static bool CanAutoSplit(Recording rec, int sectionIndex)

/// Returns the list of merge opportunities: pairs of (indexA, indexB) in the committed list.
internal static List<(int, int)> FindMergeCandidates(List<Recording> committed)

/// Returns the list of split opportunities: (index, sectionIndex) pairs.
internal static List<(int, int)> FindSplitCandidates(List<Recording> committed)
```

`CanAutoMerge` checks (all must be true):
- Same `ChainId`, sequential `ChainIndex`, both `ChainBranch == 0`
- No branch point between them (`ChildBranchPointId` on A is null)
- Same `SegmentPhase`
- Same `SegmentBodyName`
- Neither has ghosting-trigger part events
- Neither has any user-modified setting:
  - `LoopPlayback == false` on both
  - `PlaybackEnabled == true` on both
  - `Hidden == false` on both
  - `LoopIntervalSeconds == 10.0` (default) on both
  - `LoopAnchorVesselId == 0` on both
  - `RecordingGroups` null or empty on both (or identical)
  - (Future: if a `ManuallyCreated` flag is added, never merge those. Not implemented in v1.)

`CanAutoSplit` checks:
- Recording has TrackSections with `Count >= 2`
- `sectionIndex` is in range `[1, Count-1]` (can't split before first or after last)
- No ghosting-trigger part events anywhere in the recording
- No ghosting-trigger segment events anywhere in the recording
- Both halves would have `>= 1` TrackSection
- Both halves would be longer than 5 seconds

Tests:
- `CanAutoMerge_SamePhase_DefaultSettings_ReturnsTrue`
- `CanAutoMerge_DifferentPhase_ReturnsFalse`
- `CanAutoMerge_LoopEnabled_ReturnsFalse`
- `CanAutoMerge_PlaybackDisabled_ReturnsFalse`
- `CanAutoMerge_Hidden_ReturnsFalse`
- `CanAutoMerge_CustomLoopInterval_ReturnsFalse`
- `CanAutoMerge_AnchorSet_ReturnsFalse`
- `CanAutoMerge_DifferentGroups_ReturnsFalse`
- `CanAutoMerge_HasGhostingTriggerEvents_ReturnsFalse`
- `CanAutoMerge_BranchPointBetween_ReturnsFalse`
- `CanAutoMerge_DifferentBody_ReturnsFalse`
- `CanAutoSplit_NoGhostingTriggers_ReturnsTrue`
- `CanAutoSplit_HasGhostingTriggers_ReturnsFalse`
- `CanAutoSplit_HalfTooShort_ReturnsFalse`
- `FindMergeCandidates_ThreeExoSegments_ReturnsTwoPairs`

Done condition: all tests pass.

---

**Task 4.2: Merge execution**

Files: `RecordingOptimizer.cs`, `RecordingOptimizerTests.cs`

```csharp
/// Merges recording B into recording A (A absorbs B).
/// A's Points, PartEvents, SegmentEvents, TrackSections, OrbitSegments, FlagEvents
/// are extended with B's data. A's EndUT updated. B is marked for deletion.
/// Returns B's RecordingId (caller deletes files + removes from store).
internal static string MergeInto(Recording target, Recording absorbed)
```

Steps:
1. Concatenate Points (already UT-ordered)
2. Merge + re-sort PartEvents by UT
3. Merge + re-sort SegmentEvents by UT
4. Concatenate TrackSections
5. Merge OrbitSegments
6. Union FlagEvents
7. If absorbed had non-null VesselSnapshot (was chain tip), target inherits it
8. If absorbed had non-null TerminalStateValue, target inherits it (absorbed was the later segment)
9. Clear ExplicitStartUT/ExplicitEndUT to NaN on target (Points now cover the full range)
10. If absorbed had Controllers, merge (or keep target's if both non-null)
11. If absorbed had AntennaSpecs, merge (or keep target's if both non-null)
12. Invalidate GhostGeometry: set target's `GhostGeometryAvailable = false`, clear `GhostGeometryRelativePath` (geometry covers only the first half's vessel config — must be regenerated)
13. Invalidate target's CachedStats

Test:
- `MergeInto_ConcatenatesPoints` — verify points from both recordings in order
- `MergeInto_MergesPartEvents_Sorted` — verify part events re-sorted by UT
- `MergeInto_InheritsVesselSnapshot_WhenAbsorbedIsChainTip`
- `MergeInto_KeepsNullSnapshot_WhenAbsorbedIsMidChain`
- `MergeInto_InheritsTerminalState`
- `MergeInto_InvalidatesGhostGeometry`
- `MergeInto_ClearsExplicitUTRanges`

Done condition: all tests pass.

---

**Task 4.3: Split execution**

Files: `RecordingOptimizer.cs`, `RecordingOptimizerTests.cs`

```csharp
/// Splits a recording at the given TrackSection boundary index.
/// Returns the new Recording (second half). The original is mutated to keep the first half.
/// Caller must assign chain linkage, save files, add to store.
internal static Recording SplitAtSection(Recording original, int sectionIndex)
```

Steps:
1. Determine split UT from `TrackSections[sectionIndex].startUT`
2. Partition Points by UT
3. Partition PartEvents, SegmentEvents, FlagEvents by UT
4. Partition TrackSections: first half = `[0, sectionIndex)`, second half = `[sectionIndex, Count)`
5. Partition OrbitSegments by UT
6. Clone GhostVisualSnapshot to new recording (safe because `CanAutoSplit` only permits splits when NO ghosting-trigger part events exist anywhere in the recording — the vessel looks identical throughout, so the snapshot is valid for both halves)
7. New recording gets new RecordingId
8. Tag SegmentPhase on both halves from their first TrackSection's environment
9. Update both recordings' UT ranges
10. Invalidate GhostGeometry on both halves: `GhostGeometryAvailable = false`, clear `GhostGeometryRelativePath`. Caller (Task 4.5) must write new `.pcrf` files for each half, or leave geometry unavailable for regeneration on next playback.
11. Invalidate CachedStats on both

Test:
- `SplitAtSection_PartitionsPoints` — correct points in each half
- `SplitAtSection_PartitionsPartEvents`
- `SplitAtSection_ClonesSnapshot`
- `SplitAtSection_TagsPhaseFromEnvironment`
- `SplitAtSection_BothHalvesHaveValidUTRanges`

Done condition: all tests pass.

---

**Task 4.4: Chain re-indexing helper**

Files: `RecordingOptimizer.cs` (or `RecordingStore.cs`), tests

```csharp
/// Re-indexes ChainIndex for all recordings with the given ChainId.
/// Sorts by StartUT, assigns sequential indices starting from 0.
internal static void ReindexChain(List<Recording> committed, string chainId)
```

Test:
- `ReindexChain_AfterMerge_IndicesSequential`
- `ReindexChain_AfterSplit_IndicesSequential`
- `ReindexChain_PreservesChainBranch` — only re-indexes branch 0

Done condition: all tests pass.

---

**Task 4.5: Orchestration + file I/O**

Files: `RecordingStore.cs`, `ParsekScenario.cs`

Add to RecordingStore:
```csharp
/// Runs the optimization pass: find merge/split candidates, execute, clean up files.
/// Called on save load (after migrations) and optionally on recording commit.
internal static void RunOptimizationPass()
```

Flow:
1. `FindMergeCandidates` → for each pair, `MergeInto` + `ReindexChain` + `DeleteRecordingFiles` for absorbed
2. `FindSplitCandidates` → for each, `SplitAtSection` + assign chain linkage + `SaveRecordingFiles` + `ReindexChain`
3. Log summary: `"Optimization pass: merged {n} pairs, split {m} recordings"`

Integration:
- Call from `ParsekScenario.OnLoad` after existing migration passes
- Optionally call from `CommitBoundarySplit` (merge check: does the newly committed segment merge with its predecessor?)

Guard: `RecordingStore.SuppressLogging` for test compatibility.

Test: end-to-end with synthetic recordings — create 3 consecutive "exo" segments, run optimization, verify merged into 1.

Done condition: `dotnet test` passes, optimization runs on load without errors.

---

## What We're NOT Doing

- **No sub-range looping within a recording.** Each recording loops as a whole or not at all. Segmentation provides the granularity.
- **No new UI features for segment selection.** Existing per-recording loop toggle is sufficient.
- **No new playback engine changes.** `TryComputeLoopPlaybackUT` stays the same.
- **No data merging of chain segments.** Segments stay as independent recordings. Grouping is UI-only.
- **No part-event fast-forward.** Each segment has its own snapshot — no cross-segment state reconstruction needed.

---

## Why This Is Better Than Sub-Range Looping

| Concern | Segmentation approach | Sub-range looping approach |
|---------|----------------------|---------------------------|
| Playback engine changes | None | New two-phase cycle math |
| Part event state | Each segment has own snapshot | Need fast-forward or per-section cache |
| Ghost visual state | Each segment has own snapshot | Need mid-recording snapshot or rebuild |
| Overlap ghost behavior | Each segment is independent | Different-length cycles create mismatches |
| Format migration | Minimal (altitude fields) | New loop range fields + section index stability |
| UI changes | Collapsible grouping only | Segment selection UI needed |
| Testing surface | One new boundary detector | New loop math + state management |
| Backward compatibility | Fully compatible | New fields on Recording + IPlaybackTrajectory |

---

## Phase 4: Recording Optimization Pass (Merge / Split Housekeeping)

An automatic post-recording optimization pass that merges or splits chain segments to produce clean, useful segmentation. Runs as housekeeping — no player interaction needed.

### Merge: consolidate redundant segments

Two or more consecutive chain segments can be merged when:
- Same `ChainId`, sequential `ChainIndex`, `ChainBranch == 0`
- No branch point between them (no docking/undocking/EVA at the boundary)
- Same or compatible `SegmentPhase` (e.g., two adjacent "exo" segments)

Merge operation:
1. Concatenate `Points` lists (already UT-ordered)
2. Merge `PartEvents` lists (re-sort by UT)
3. Merge `SegmentEvents` lists
4. Concatenate `TrackSections` lists
5. Merge `OrbitSegments` lists
6. Use first segment's `GhostVisualSnapshot` (vessel looked like this at the start of the combined segment)
7. `VesselSnapshot`: mid-chain segments have `VesselSnapshot = null` (ghost-only). If the absorbed segment was the final chain segment (non-null `VesselSnapshot`), the merged result inherits it (merged result is now the chain tip). Otherwise stays null.
8. Union `FlagEvents`
9. Adopt first segment's `RecordingId`, update UT ranges
10. Re-index `ChainIndex` for remaining segments in the chain
11. Delete absorbed segment's sidecar files (`.prec`, `_ghost.craft`, `.pcrf`)
12. Save merged segment's updated sidecar files

**Use cases:**
- Multiple "exo" segments created by SOI transitions (Kerbin exo → Mun SOI exo → they're both just "transit in space")
- Brief atmosphere oscillation during aerobraking creating tiny segments
- On-rails / off-rails cycling during time warp creating redundant ExoBallistic segments
- Any sequential segments where the player would never want different loop settings

**Merge policy (automatic) — user intent is sacred:**

Hard constraints (never merge if any of these are true):
- Either segment has ghosting-trigger part events (`GhostingTriggerClassifier.HasGhostingTriggerEvents`) — ghost snapshot would be wrong for the second half
- Different `SegmentPhase` (don't merge "atmo" with "exo" or "approach" with "exo")
- Different `SegmentBodyName` (preserve body-awareness)
- Branch point between them (docking, undocking, EVA at the boundary)
- Either segment has **any user-modified setting** that differs from defaults:
  - `LoopPlayback = true` on either segment (player chose to loop this specific segment)
  - `PlaybackEnabled = false` on either segment (player chose to hide this segment)
  - `Hidden = true` on either segment
  - `LoopIntervalSeconds` differs from default (player tuned the loop timing)
  - `LoopAnchorVesselId != 0` on either segment (player configured anchor)
  - Different `RecordingGroups` membership (player organized them differently)
- Either segment was created by a future manual split operation (needs a `ManuallyCreated` flag or similar — segments the player explicitly created should never be auto-merged back)

Soft constraints (prefer not to merge):
- Minimum segment duration guard: prefer merging tiny segments (< 10s) into neighbors over merging two substantial segments
- Merge the smaller segment into the larger, not vice versa

**The principle: if the player has touched a segment's settings in any way, that segment is off-limits for auto-merge. Only merge segments that are in pristine default state.**

### Split: break apart segments at TrackSection boundaries

Split a single recording into chain segments at internal TrackSection boundaries. More constrained than merge because each new segment needs a valid ghost snapshot.

**Split is safe when:**
- No ghosting-trigger part events exist across the split boundary (vessel visual state hasn't changed — `GhostingTriggerClassifier.HasGhostingTriggerEvents` can be adapted to check a UT range)
- The existing `GhostVisualSnapshot` is reusable for both halves (same vessel configuration)

**Split is NOT safe when:**
- Part events between the recording start and the split point changed vessel geometry (fairings jettisoned, stages separated, etc.)
- The second half would need a different ghost snapshot than the first half

**Split operation (when safe):**
1. Create new Recording for the second half
2. Partition `Points` at the split UT
3. Partition `PartEvents`, `SegmentEvents`, `FlagEvents` by UT
4. Partition `TrackSections` at the boundary
5. Partition `OrbitSegments` by UT
6. Clone `GhostVisualSnapshot` to the new segment (safe because the policy restricts splitting to recordings with no ghosting-trigger part events anywhere — the vessel looks the same throughout, so the snapshot is valid for both halves)
7. Assign chain linkage (`ChainId`, `ChainIndex`, `ChainBranch`)
8. Tag `SegmentPhase` based on the TrackSection environment at the split point
9. Write new sidecar files, update existing segment's files

**Use cases:**
- Tree-mode recordings that suppressed chain splits: after recording ends, post-process to split at TrackSection boundaries where it's safe
- Legacy monolithic recordings from before altitude splits were added
- Recordings where the player wants finer-grained loop control than was captured at recording time

**Split policy (automatic):**
- Only split at TrackSection boundaries where `SegmentEnvironment` changes
- Only split when no ghosting-trigger part events exist in the recording (simple case first)
- Never split segments shorter than a minimum duration (e.g., < 5s)

### When does the optimization pass run?

- **On recording commit** (`StopRecording` / `CommitBoundarySplit`): merge pass checks if the newly committed segment should merge with its predecessor
- **On save load** (`ParsekScenario.OnLoad`): one-time pass over all recordings to merge redundant segments. Runs after v2→v3 / v4→v5 migrations.
- **On explicit player action** (future): "Optimize Recordings" button in settings, for players who want to clean up legacy saves

### Implementation

**Files to modify:**
- New file: `RecordingOptimizer.cs` — pure static methods for merge/split logic
  - `CanMergeSegments(Recording a, Recording b) → bool` — pure, testable
  - `MergeSegments(Recording target, Recording absorbed) → void` — mutates target, returns absorbed ID for deletion
  - `CanSplitAtTrackSection(Recording rec, int sectionIndex) → bool` — checks part event safety
  - `SplitAtTrackSection(Recording rec, int sectionIndex) → Recording` — returns new segment

- `RecordingStore.cs` — orchestration: find merge/split candidates, execute, update indices
  - `RunOptimizationPass(List<Recording> recordings)` — scans for merge/split opportunities, executes
  - File I/O: delete absorbed segment files, write new split segment files

- `ChainSegmentManager.cs` — `ReindexChain(string chainId)` helper to fix chain indices after merge/split

- `ParsekScenario.cs` — call optimization pass on load

**Tests:**
- `CanMergeSegments` — same phase/different phase, branch points, loop settings, body changes
- `MergeSegments` — point concatenation, event merging, UT range updates, chain re-indexing
- `CanSplitAtTrackSection` — with/without ghosting triggers, minimum duration
- `SplitAtTrackSection` — point partitioning, event partitioning, snapshot cloning
- Round-trip: merge then verify the merged recording plays back identically to the original sequence

---

## Review-Informed Updates

### Changes from first review (now incorporated)

1. **Lower altitude floor clamp to 5,000 m** (from 10,000 m). 10 km on Gilly (13 km radius) is 0.77 body-radii — disproportionately high. 5 km is 0.38 body-radii, still generous.

2. **Do NOT make altitude threshold a setting.** An opinionated default (`body.Radius * 0.15` clamped `[5_000, 200_000]`) is better than exposing a tunable that requires orbital mechanics knowledge.

3. **`ClearBoundaryFlags` must also clear altitude state.** Add to Phase 1 file change list.

4. **`TagSegmentPhaseIfMissing` and `CommitVesselSwitchTermination` need "approach" handling.** Currently tags airless bodies as `"space"` unconditionally — must distinguish `"approach"` vs `"exo"` based on altitude.

5. **UI phase styling for "approach".** Needs distinct color in `ParsekUI.cs` (alongside existing "atmo" and "space" styles).

6. **Recording-start seeding.** `wasAboveAltitudeThreshold` must be seeded in `StartRecording`, not just on SOI change.

7. **Phase 2 scope clarification.** Chain grouping already exists in `DrawChainBlock`. Phase 2 is: add bulk loop toggle to existing chain header + "partial" loop indicator. Not building grouping from scratch.

8. **Auto-enable `LoopPlayback` for "approach" and "atmo" segments.** Consider setting `LoopPlayback = true` at commit time for segments tagged "approach" or "atmo". Changes the UX from "opt-in to loop interesting segments" to "opt-out of looping boring segments." One-line change in segment commit logic.

9. **No existing recordings can be retroactively split.** Monolithic Mun landings from before this feature require re-recording. Acceptable, but the Phase 4 optimization pass (split) addresses this for cases where no part events cross the boundary.

---

## Open Questions

1. **Altitude threshold tuning.** `body.Radius * 0.15` clamped to `[5_000, 200_000]` is the default. May need adjustment after in-game testing. Do not make it a setting.

2. **Tree mode.** Altitude splits are suppressed in tree mode (same as atmosphere splits). If tree-mode recordings need selective looping, that's a future concern — Phase 4 split could post-process them.

3. **Multiple altitude crossings.** Hysteresis + minimum segment guard handles this. If a vessel does 5 low passes, 5 segments is correct (each independently loopable). Phase 4 merge can consolidate redundant ones if needed.

4. **Ascending vs descending.** Tag `"approach"` for descending below threshold, `"exo"` for ascending above. Mirrors atmosphere boundary convention (name the completed segment).

5. **Auto-loop default.** Should "approach" and "atmo" segments auto-enable `LoopPlayback`? Dramatically improves out-of-box experience but changes existing behavior for atmosphere splits.

6. **Optimization pass performance.** On save load with many recordings, the merge/split scan should be bounded. O(n) scan with O(1) per-pair merge check is fine. Avoid O(n^2) patterns.

7. **`ManuallyCreated` flag.** Phase 4 merge needs a way to identify segments the player explicitly created (via future manual split). Auto-merge must never recombine these. Could be a bool on Recording, or inferred from the absence of a boundary-split origin marker.

8. **Hysteresis distance scaling for altitude boundary.** The atmosphere boundary uses a fixed 1000m distance. For altitude splits, consider scaling: `max(1000, threshold * 0.02)`. On Gilly (5km threshold), 1km is 20% of the threshold — generous. On Tylo (90km threshold), 1km is negligible. Scaling keeps the hysteresis proportionate.
