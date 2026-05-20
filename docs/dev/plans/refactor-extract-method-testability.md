# Refactor Inventory: Extract-Method, Testability Seams, and Test Gaps

## Scope and intent

This is a READ-ONLY inventory of behavior-preserving refactor opportunities in the
recording/rendering subsystem. It covers three kinds of work, all additive plus
behavior-preserving:

1. **Extract-method**: cohesive code blocks inside large multi-responsibility methods
   that can be lifted into a private/internal-static helper called from the exact same
   position.
2. **Testability seams**: pure logic currently buried in a large method that could
   become `internal static` and get a unit test.
3. **Test gaps**: existing untested decision points (guards, classifiers, transitions)
   worth characterization tests even without an extraction.

Every item here is a candidate, not a committed change. Each one must get its own
focused proposal and a fresh-context review against
`docs/dev/refactor-guidelines.md` (the 13-item checklist) before it is implemented.
A confident, narrow extraction beats a sprawling one: when in doubt, scale back.

**Hard constraint reminder (checklist item 13):** never change a pre-existing member's
access modifier to make an extraction `internal static`. If lifting a block to
`internal static` would require touching a pre-existing `private` type or member, the
correct answer is a `private static` (or `private`) helper that cannot be unit-tested.
A private helper that is untestable is strictly better than an `internal` one bought by
violating the no-access-change rule.

**Line numbers are approximate.** A separate in-flight "narrow pass" PR
(`refactor-recording-rendering-pass`) is editing several of these same files and will
shift line numbers. Anchor on the function name plus the block description, not the line
number, when relocating an item.

### Excluded: the in-flight narrow pass

The narrow pass already implements the following items. They are **NOT** re-proposed
here:

- H1 dead ternary in `AnchorPropagator` failTag.
- H2 redundant format/gen re-parse in `RecordingTreeRecordCodec.LoadRecordingPlaybackAndLinkage`.
- H3 read-parse-assign idiom extraction in `RecordingTreeRecordCodec` load path.
- M1 dedup of the two `switch(referenceFrame)` pose-dispatches in `RelativeAnchorResolver`.
- M2 `ShouldSkipPostPositionPipeline` + `ResolveRenderSurface` per-frame preamble pairing in `GhostPlaybackEngine`.
- M3 capped-dedup-set housekeeping in `SmoothingPipeline`.
- M4 save-helper comment-delimited block split in `RecordingTreeRecordCodec` save path.
- L2 duplicate `IsFinite` delegation in `TrajectoryMath`.

---

## Priority summary

| Priority | Count |
|----------|-------|
| High     | 6     |
| Medium   | 8     |
| Low      | 5     |

Top extract-method opportunities (detail below):

1. `RecordingStore.RunOptimizationSplitPass` -> lift the Re-Fly-deferred split-candidate selection loop into a pure picker.
2. `RecordingStore.RunOptimizationSplitPass` -> lift the `second`-recording identity-copy block into `CopySplitIdentityFields`.
3. `TrajectoryMath.ComputeStats` -> lift the per-point distance accumulation block into `AccumulatePointDistance`.
4. `TrajectoryMath.ComputeStats` -> lift the per-point max-range computation into `ComputePointRangeFromStart`.
5. `IncompleteBallisticSceneExitFinalizer.Apply` -> lift the terminal surface-metadata block into `ApplyTerminalSurfaceMetadata`.

Top testability seams:

1. `TrajectoryMath` distance/range blocks (above) become directly unit-testable pure helpers (frame-aware Relative vs surface-haversine math).
2. `RecordingStore.RunOptimizationSplitPass` deferred-candidate picker becomes a pure function over `(candidates, recordings, deferredId)`.
3. `BallisticExtrapolator.ChooseEarliestEvent` / `CreateSegment` already pure `private static`; promote to `internal static` for direct unit tests.

Top test gaps (characterization tests, no extraction needed):

1. `FlightRecorder.TryClassifyAeroSurfaceState` and `TryClassifyControlSurfaceState`: untested `internal static` event/field/deflection classifiers.
2. `RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf`: untested `internal static` branch-point-move decision.
3. `BallisticExtrapolator.ChooseEarliestEvent`: untested earliest-event tie-break ordering (Destroyed vs Horizon vs SOI).

### Flagged for a dedicated decomposition plan (not a one-shot extraction)

- `GhostPlaybackEngine.UpdatePlayback` (~613 lines) and its callee `RenderInRangeGhost`
  (~348 lines): per-frame hot path, dozens of interleaved guard branches with
  `continue`/early-exit and shared mutable locals (`state`, `ghostActive`). Do not
  attempt a one-shot carve; see the "dedicated plan" note in the GhostPlaybackEngine
  section.
- `BallisticExtrapolator.Extrapolate` (~346 lines): single `while` loop with
  interleaved mutation of loop carry-state. Already covered end-to-end by 25 tests, so
  decomposition is low-value and high-risk; treat as a dedicated plan only if it ever
  needs structural change.

---

## RecordingStore.cs

### HIGH - RunOptimizationSplitPass: extract the deferred split-candidate picker

- **Function:** `RunOptimizationSplitPass(List<Recording> recordings)` (private static, ~L4284).
- **Block:** the `chosen` selection logic (~L4312-4345): when no Re-Fly id is deferred,
  `chosen = 0`; otherwise walk `splitCandidates` skipping any whose recording id equals
  `deferredActiveReFlyId`, counting `deferredCandidatesThisIter`. Returns the chosen index
  and the deferred-observed count.
- **Proposed helper:** `internal static int ChooseSplitCandidateIndex(IReadOnlyList<(int,int)> splitCandidates, IReadOnlyList<Recording> recordings, string deferredActiveReFlyId, out int deferredObserved)`.
- **Why behavior-preserving:** the block is a pure read over `splitCandidates`,
  `recordings`, and a string id; it produces `chosen` and a counter with no side effects.
  Called from the exact same position (item 2). No control-flow change: the outer
  `if (chosen < 0) break;` stays in the loop (item 4). No mutation hazard with later blocks
  (item 5) because it only reads.
- **Testability:** YES, genuinely pure. Inputs: candidate index list, recordings list,
  deferred id. Output: chosen index plus deferred-observed count. No instance-field access.
  `Recording` is already `internal`-visible to tests (used widely in existing tests), and
  the tuple list element type is the optimizer's existing public-shape return. Confirm the
  tuple element type does not force a `private`-type parameter; if it does, fall back to
  passing the candidate recording ids as a `List<string>` projected at the call site, or
  scale back to `private static`.
- **Risk:** low (cold path, optimizer pass). **Effort:** small. **Priority:** high.

### HIGH - RunOptimizationSplitPass: extract the split-identity copy block

- **Function:** same, ~L4352-4377.
- **Block:** the run of assignments copying identity/lineage fields from `original` to
  `second` (RecordingId assignment via `Guid.NewGuid`, ChainId/TreeId/VesselName/
  VesselPersistentId/PreLaunch*/RecordingGroups/CreatingSessionId/ProvisionalForRpId/
  SupersedeTargetId/SwitchSegmentSessionId). Note `RecordingGroups` is deep-copied.
- **Proposed helper:** `private static void CopySplitIdentityFields(Recording original, Recording second)` (instance-free, mutates the passed `second`).
- **Why behavior-preserving:** a straight-line run of field copies with no branching and
  no reads of state mutated later in the method (item 5: the BranchPoint block below reads
  `original.ChildBranchPointId`, which this block does not touch). Same call position
  (item 2). The `second.RecordingId = Guid.NewGuid()...` non-determinism is identical
  whether inline or in the helper.
- **Testability:** NOT a pure value-out function (it mutates `second`), so propose
  `private static`. It could be `internal static` for an assert-the-copy test since
  `Recording` is test-visible, but the `Guid.NewGuid()` line makes one field
  non-deterministic; if tested, the helper would need the new id passed in or the test
  asserts only the copied fields. Keep `private static` unless a test is clearly worth it.
- **Risk:** low. **Effort:** small. **Priority:** high.

### MEDIUM - RunOptimizationSplitPass: extract the branch-point-parent retarget block

- **Function:** same, ~L4416-4447.
- **Block:** when `movedChildBranchPointId` is set, the nested loop over `committedTrees`
  -> `BranchPoints` -> `ParentRecordingIds` that retargets `original.RecordingId` to
  `second.RecordingId`.
- **Proposed helper:** `private static void RetargetMovedBranchPointParent(string treeId, string movedChildBranchPointId, string oldRecordingId, string newRecordingId)` (reads/mutates `committedTrees`, so instance-static on the store, not pure).
- **Why behavior-preserving:** the block is a self-contained guarded mutation; the
  surrounding code already gates entry with
  `if (!string.IsNullOrEmpty(movedChildBranchPointId) && !string.IsNullOrEmpty(original.TreeId))`.
  Moving that guard inside or keeping it at the call site are both valid; keep it at the
  call site to preserve exact branch shape (item 3). No later block reads
  `ParentRecordingIds`.
- **Testability:** touches the static `committedTrees` collection, so `private static`
  on `RecordingStore`. Not a clean pure seam.
- **Risk:** low. **Effort:** small. **Priority:** medium.

### TEST GAP - ShouldMoveChildBranchPointToSplitSecondHalf

- **Function:** `internal static bool ShouldMoveChildBranchPointToSplitSecondHalf(string treeId, string childBranchPointId, double secondHalfStartUT)` (~L4497).
- **Gap:** already `internal static` and pure-ish, but no test references it (confirmed:
  zero hits in `Source/Parsek.Tests`). It governs whether a child branch point moves to the
  second half of an optimizer split (the Re-Fly atmo/exo-after-staging-branch case noted in
  the comment at ~L4385-4397). A characterization test would pin: branch-point UT before
  vs after `secondHalfStartUT`, null/empty id short-circuit.
- **Effort:** small. **Priority:** medium (governs tree topology correctness across splits).

---

## TrajectoryMath.cs

### HIGH - ComputeStats: extract per-point distance accumulation

- **Function:** `internal static RecordingStats ComputeStats(Recording rec, Func<string,double[]> bodyLookup = null)` (~L618).
- **Block:** the per-point `distanceTravelled` accumulation (~L668-704): for `i > 0` same-body
  points not inside an orbit segment, dispatch on the mid-UT `TrackSection.referenceFrame` to
  either a Relative dx/dy/dz Euclidean delta or a haversine surface distance plus altitude
  delta.
- **Proposed helper:** `internal static double ComputePairwiseTravelDistance(in TrajectoryPoint prev, in TrajectoryPoint cur, ReferenceFrame frame, double bodyRadius)`.
- **Why behavior-preserving:** the block is a pure computation of one pairwise distance from
  two points, a frame tag, and a radius. The caller keeps the orbit-segment guard
  (`FindOrbitSegment(... midUT) != null`) and the section lookup at the call site so the
  branch shape is unchanged (item 3). Same call position (item 2); accumulates into the same
  `stats.distanceTravelled +=`. No mutation hazard: the range block that follows reads only
  `stats.maxRange` and point fields (item 5).
- **Testability:** YES, genuinely pure (inputs in, double out, no instance state). This
  mirrors the existing `AccumulateOrbitSegmentStats` / `DeterminePrimaryBody` extractions
  already tested in `ComputeStatsExtractedTests.cs`. `TrajectoryPoint` and `ReferenceFrame`
  are already test-visible (used throughout the test suite). The CLAUDE.md "metres-as-degrees"
  Relative-frame contract makes this a high-value test target: a unit test pins that Relative
  frames use raw dx/dy/dz and Absolute frames use haversine.
- **Risk:** low (cold path, called on commit/UI). **Effort:** small. **Priority:** high.

### HIGH - ComputeStats: extract per-point max-range computation

- **Function:** same, ~L706-736.
- **Block:** the `body == body0` max-range computation: dispatch on first-point frame vs
  current-point frame to either an Euclidean dx/dy/dz range (both Relative), zero (current
  Relative only), or a haversine range from the first point (Absolute).
- **Proposed helper:** `internal static double ComputePointRangeFromStart(in TrajectoryPoint start, in TrajectoryPoint cur, ReferenceFrame startFrame, ReferenceFrame curFrame, double bodyRadius)`.
- **Why behavior-preserving:** pure computation of one range value; the `body == body0`
  outer guard and `if (range > stats.maxRange)` update stay at the call site. No reordering,
  no logic change (item 3).
- **Testability:** YES, pure. Pins the three-way frame dispatch (the
  `else if (pointFrame == Relative) range = 0.0` branch is a subtle correctness case worth a
  characterization test).
- **Risk:** low. **Effort:** small. **Priority:** high.

### LOW - ComputeStats: bodyData unpacking is fine as-is

- The `double bodyRadius = bodyData[0]` indexing is trivial; not worth a helper. Noted so a
  later implementer does not over-extract.

---

## IncompleteBallisticSceneExitFinalizer.cs

### HIGH - Apply: extract the terminal surface-metadata block

- **Function:** `private static void Apply(Recording recording, IncompleteBallisticFinalizationResult result, string logContext)` (~L2163).
- **Block:** the surface-metadata block (~L2224-2255): when terminalState is Landed/Splashed,
  set `TerminalPosition` + `TerrainHeightAtEnd` from the result (with the `ghostOnlySnapshot`
  warn-and-keep branch), else null both.
- **Proposed helper:** `private static void ApplyTerminalSurfaceMetadata(Recording recording, in IncompleteBallisticFinalizationResult result, bool ghostOnlySnapshot, string logContext)`.
- **Why behavior-preserving:** a self-contained conditional that writes two recording fields
  based on the result. It runs after the snapshot block and before `MarkFilesDirty()`; no
  later code re-reads `TerminalPosition`/`TerrainHeightAtEnd` inside `Apply` (item 5). Same
  call position (item 2). Logging is observational and preserved verbatim (item 9).
- **Testability:** mutates `recording`, so `private static` (NOT pure). Would require
  `IncompleteBallisticFinalizationResult` to be test-visible to make it `internal static`;
  if that type is `private`/internal-to-file, keep `private static` per item 13. Do not change
  its access to gain testability.
- **Risk:** low (scene-exit, cold path). **Effort:** small. **Priority:** high.

### MEDIUM - Apply: extract the snapshot-application block

- **Function:** same, ~L2179-2212.
- **Block:** the vessel-snapshot vs ghost-only-snapshot application (the two
  `if (result.vesselSnapshot != null)` / `else if (result.ghostVisualSnapshot != null)`
  branches with their `CreateCopy()` and ConfigNode-equivalence warn logs).
- **Proposed helper:** `private static void ApplyTerminalSnapshots(Recording recording, in IncompleteBallisticFinalizationResult result, bool ghostOnlySnapshot, string logContext)`.
- **Why behavior-preserving:** the block computes nothing the surface-metadata block depends
  on except `ghostOnlySnapshot`, which is computed once before both blocks and passed in
  unchanged. No reordering. The ghost-snapshot-mode recompute (~L2214-2222) reads
  `recording.GhostVisualSnapshot`, which this block sets, so the helper must run in the same
  position before the mode recompute (item 5).
- **Testability:** `private static` (mutates recording, reads file-local result type).
- **Risk:** low. **Effort:** medium (two nested branches, careful equivalence with originals
  under item 10). **Priority:** medium.

---

## FlightRecorder.cs

### TEST GAP (HIGH) - TryClassifyAeroSurfaceState / TryClassifyControlSurfaceState

- **Functions:** `internal static bool TryClassifyAeroSurfaceState(PartModule, out bool isDeployed, out bool isRetracted)` (~L2313, ~117 lines) and the adjacent `TryClassifyControlSurfaceState` (~L2431).
- **Gap:** both are already `internal static`, both have zero test references. They classify
  deploy/retract state via three fallback strategies: (1) event-name/guiName keyword scan +
  `TryClassifyLadderStateFromEventActivity`, (2) boolean field probe over a fixed name list,
  (3) deflection float probe with NaN/Inf rejection and a `> 0.01f` threshold.
- **Constraint:** the `PartModule.Events` path needs a live KSP `PartModule`, which xUnit
  cannot build. But the boolean-field and deflection-float fallback paths route through
  `TryReadModuleBoolField` / `TryReadModuleFloatField`, which may already have test seams (the
  ladder-state classifier `TryClassifyLadderStateFromEventActivity` is already tested in
  `FlightRecorderExtractedTests.cs` / `PartEventTests.cs`). The genuinely-pure sub-decision is
  the event-keyword classification and the deflection threshold logic. If those cannot be
  reached without a live `PartModule`, this is an **in-game test** in `RuntimeTests.cs` (per
  the memory note on Unity-runtime test coverage), not an xUnit test.
- **Testability seam (smaller, pure):** the event-name keyword matching (~L2335-2345, the two
  `isDeployEvent`/`isRetractEvent` boolean expressions) could be lifted to
  `internal static void ClassifyAeroEventName(string evtName, string guiName, out bool isDeploy, out bool isRetract)` and unit-tested directly. Pure: two strings in, two bools out.
  This is the cleanest pure carve in this method.
- **Effort:** small (keyword helper) / medium (full in-game test). **Priority:** high for the
  keyword-classifier seam; medium for the in-game characterization test.

### MEDIUM - UpdateAnchorDetection: extract the close-and-reopen-section sequence

- **Function:** `private void UpdateAnchorDetection(Vessel v)` (~L5727, ~169 lines).
- **Block:** the repeated sequence that appears three times (relative-exit-landing ~L5741-5757,
  relative-enter ~L5825-5854, relative-exit-distance ~L5867-5892): sample a boundary point,
  capture old anchor id/pid, flip `isRelativeMode`, clear/set the recording anchor,
  `CloseCurrentTrackSection(boundaryUT)`, resolve env from `environmentHysteresis`,
  `StartNewTrackSection(env, frame, boundaryUT)`, `ActivateHighFidelitySampling(...)`, append a
  seam point, and log.
- **Proposed helper:** the three call sites differ in frame (Absolute vs Relative), reason
  string, anchor set vs clear, and the relative-enter seed-failure early-return. A SINGLE
  shared helper would have to thread all of those as parameters and would obscure the
  seed-failure `return` (item 4). RECOMMENDATION: do NOT fully unify. The safe, narrow carve is
  the common tail `CloseCurrentTrackSection -> resolve env -> StartNewTrackSection -> Activate
  -> AppendSectionStartSeamPoint` into
  `private void RotateToNewTrackSection(double boundaryUT, ReferenceFrame frame, string activationReason, string seamReason)`,
  applied only to the two exit cases (landing and distance) which are structurally identical
  except their reason strings. The relative-enter case has the seed-failure branch and must
  stay inline.
- **Why behavior-preserving (for the two exit cases only):** the tail is straight-line, both
  exit blocks call exactly the same five methods with the same env resolution; deduplication is
  semantically identical (item 10). The `isRelativeMode = false; ClearCurrentRecordingAnchor();`
  prefix differs in log content, so leave that at the call site.
- **Testability:** instance method (touches `environmentHysteresis`, `currentAnchor*`, calls
  instance methods), so `private`, NOT testable in xUnit. This is a readability/dedup carve,
  not a testability seam.
- **Risk:** medium (recorder hot-ish path, frame-contract correctness; the
  `CloseCurrentTrackSection` discard rules are subtle, see `FlightRecorderExtractedTests`).
  **Effort:** medium. **Priority:** medium. Requires its own focused proposal and review.

---

## BackgroundRecorder.cs

### MEDIUM - OnBackgroundPhysicsFrame: extract the environment-transition block

- **Function:** `public void OnBackgroundPhysicsFrame(Vessel bgVessel)` (~L1929, ~271 lines).
- **Block:** the environment-transition handling (~L2014-2041): classify the raw env from the
  vessel, call `state.environmentHysteresis.Update(rawEnv, ut)`, and on a transition
  activate high-fidelity sampling, log, capture the boundary point, close the current BG track
  section, start a new Absolute section, and seed the boundary point.
- **Proposed helper:** `private void HandleBackgroundEnvironmentTransition(BackgroundVesselState state, Vessel bgVessel, uint pid, double ut)`.
- **Why behavior-preserving:** the block is a self-contained guarded action gated by
  `if (state.environmentHysteresis != null)`. It runs in fixed position before
  `UpdateDebrisProximityState` / `UpdateBackgroundAnchorDetection`; none of those re-read the
  hysteresis transition flag (item 5). The `bgVessel.packed` early-return that protects the
  eccentric-orbit invariant (CLAUDE.md S16) is upstream and is NOT moved (item 4) - critical:
  the proposal must keep the extracted call strictly below the existing packed/isOnRails gates.
- **Testability:** instance method touching `state` and calling instance helpers
  (`CloseBackgroundTrackSection`, `StartBackgroundTrackSection`, `SeedBackgroundBoundaryPoint`),
  so `private`, not a pure seam. The pure sub-decision (the `ClassifyBackgroundEnvironment`
  argument mapping from a vessel) is already a separate `internal static`
  `ClassifyBackgroundEnvironment` and is tested elsewhere.
- **Risk:** medium (per-frame BG path, but the block only fires on a transition;
  no-allocation concern is minimal since it is the cold branch). **Effort:** medium.
  **Priority:** medium. Must be reviewed against the S16 invariant
  (`EccentricOrbitOptimizerInvariantTests`).

### LOW - InitializeLoadedState: too tangled for a one-shot carve

- **Function:** `private void InitializeLoadedState(...)` (~L3705, ~319 lines).
- **Assessment:** large but heavily interleaved: it threads `treeRecForSeed`,
  `hasTreeRecording`, `initialTrajectoryPoint`, and `debrisContractApplies` through a long
  debris-seed decision with multiple early `Warn` paths and a live-anchor-pose construction.
  The debris-seed sub-block (~L3789 onward) reads several locals computed earlier in the method.
  A clean extraction would need many parameters and risks item-5 mutation-order hazards.
  RECOMMENDATION: leave for a dedicated decomposition plan if it is ever touched; do not carve
  opportunistically. **Priority:** low.

---

## BallisticExtrapolator.cs

### LOW (testability) - Promote ChooseEarliestEvent and CreateSegment to internal static

- **Functions:** `private static EventCandidate ChooseEarliestEvent(...)` (~L631) and
  `private static OrbitSegment CreateSegment(...)` (~L607).
- **Seam:** both are already pure `private static`. Promoting to `internal static` enables
  direct unit tests of the earliest-event tie-break ordering (Destroyed vs Horizon vs
  ParentExit vs ChildEntry) and the segment-construction frame math, instead of only the
  current 25 end-to-end `Extrapolate` tests.
- **Constraint (item 13):** verify the parameter/return types (`EventCandidate`,
  `OrbitSegment`, `TwoBodyOrbit`, `ExtrapolationBody`) are not `private`. `OrbitSegment` is
  test-visible. If `EventCandidate` or `TwoBodyOrbit` is a `private`/file-local type, do NOT
  widen its access; leave the helper `private static` and rely on the existing integration
  tests. This item is conditional on those types already being `internal`+.
- **Risk:** low (no behavior change, access widening only). **Effort:** small.
  **Priority:** low (already covered indirectly).

### NOT PROPOSED - Extrapolate loop-body decomposition

See "Explicitly NOT proposed" below.

---

## GhostVisualBuilder.cs

### MEDIUM (testability seam) - BuildHeatMaterialStates: extract the heat-color math

- **Function:** `private static List<HeatMaterialState> BuildHeatMaterialStates(...)` (~L4389, ~146 lines).
- **Block:** the cold/hot/medium color and emission computation (~L4488-4514): given a cold
  color and whether a color/emissive property exists, compute `hotColor` (Lerp toward
  `HeatTintColor` at 0.45), `hotEmission`, and the medium midpoints, then populate a
  `HeatMaterialState`.
- **Proposed helper:** `internal static HeatMaterialState BuildHeatMaterialState(Material materialClone, string colorProperty, Color coldColor, string emissiveProperty)` OR a purer
  `internal static (Color hot, Color medium, Color hotEmission, Color mediumEmission) ComputeHeatColorRamp(Color coldColor, bool hasColorProperty, bool hasEmissiveProperty)`.
- **Why behavior-preserving:** the color ramp is pure math (`Color.Lerp`, constant tint/emission
  colors). The surrounding Unity material cloning (`new Material(source)`, `renderer.materials`)
  stays inline. The ramp helper takes the cold color and two booleans and returns four colors.
- **Testability:** the ramp variant is genuinely pure (`Color`, `Color.Lerp`, and the constant
  `HeatTintColor`/`HeatEmissionColor` are usable without a live Unity scene since `Color` is a
  plain struct). Confirm `HeatTintColor`/`HeatEmissionColor` are not `private` instance state -
  they are static readonly fields; if `private`, pass them in or keep the helper `private
  static` (item 13). NOTE: the rest of `BuildHeatMaterialStates` (Renderer/Material enumeration)
  cannot be xUnit-tested and should stay as-is.
- **Risk:** low (the ramp is leaf math; the hot path concern is the per-renderer loop, which is
  unchanged). **Effort:** small. **Priority:** medium.

### LOW - Other large GhostVisualBuilder methods are Unity-bound

- `ApplyVariantTextureRules` (~L3460), `GenerateFairingConeMesh` (~L3755),
  `TryBuildParachuteInfo` (~L4543), `BuildReentryFireParticleSystem` (~L6228),
  `SpawnExplosionFx` (~L6882): all are dominated by Unity mesh/particle/material construction
  with little extractable pure logic. `GenerateFairingConeMesh` has pure vertex math that could
  in principle be lifted, but the payoff is low and the risk of an off-by-one in vertex ordering
  is real. RECOMMENDATION: not proposed; if touched, prefer in-game tests over extraction.
  **Priority:** low.

---

## RecordingTree.cs

### MEDIUM (testability seam) - PruneRejectedRecordingReferences: extract the per-recording reference clear

- **Function:** `private static void PruneRejectedRecordingReferences(RecordingTree tree, HashSet<string> rejectedRecordingIds)` (~L263, ~95 lines).
- **Block:** the `foreach (Recording rec in tree.Recordings.Values)` loop (~L321-352) that
  clears `ParentRecordingId`, `DebrisParentRecordingId`, `ParentBranchPointId`,
  `ChildBranchPointId` when they reference a rejected recording or a removed branch point.
- **Proposed helper:** `internal static void ClearRejectedRecordingReferences(Recording rec, HashSet<string> rejectedRecordingIds, HashSet<string> removedBranchPointIds)`.
- **Why behavior-preserving:** per-recording field clears with no cross-recording state; the
  loop iterates and calls the helper once per recording (item 8: this is NOT splitting a loop,
  it is extracting the loop BODY into a helper called once per iteration, which is allowed).
  Same order. The two outer blocks (whole-tree drop, branch-point removal) stay in the parent
  and produce `removedBranchPointIds`, passed in unchanged.
- **Testability:** YES, pure given `(rec, rejectedIds, removedBranchPointIds)`. `Recording` is
  test-visible. A unit test pins each of the four field-clear conditions independently and the
  `removedBranchPointIds == null` short-circuit.
- **Risk:** low (load-time, cold). **Effort:** small. **Priority:** medium.

### LOW - PruneRejectedRecordingReferences: whole-tree-drop block

- The root-rejected whole-tree-drop block (~L270-289) is a single guarded clear-and-log. It
  could be `private static DropEntireTree(tree, rootRecordingId)` but the payoff is marginal
  (one call site). **Priority:** low.

---

## Rendering/SmoothingPipeline.cs

### MEDIUM - FitAndStorePerSection: extract the per-section fit body

- **Function:** `internal static void FitAndStorePerSection(Recording rec)` (~L108, ~141 lines).
- **Block:** the per-section loop body (~L131-234): the `ShouldFitSection` skip, the inertial
  frame-tag decision, body resolution with the `ReferenceEquals(body, null)` test seam, the
  outlier classification, the `CatmullRomFit.Fit` call with timing, validity check, spline
  store, and the per-section logging.
- **Proposed helper:** `private static void FitAndStoreSection(Recording rec, int sectionIndex, ref int fitOk, ref int fitFailed, ref int skipped)` (the three `ref int` counters preserve the post-loop summary log).
- **Why behavior-preserving:** this is extracting the loop BODY into a helper invoked once per
  iteration (allowed; not loop-splitting per item 8). The `continue` statements inside the
  current body become `return` in the helper (item 4: each `continue` maps to an early `return`
  in the void helper, with the counter incremented before the return exactly as today). Same
  call order. The post-loop summary log reads the three counters, threaded via `ref`.
- **Constraint:** the `continue`-to-`return` mapping must be verified carefully for each of the
  three skip/fail paths (item 4). The `ref int` counters must be incremented at the identical
  point relative to the early exit.
- **Testability:** touches static `SectionAnnotationStore` and the static config; `private
  static`, not a pure seam. This is a length/cohesion carve.
- **Risk:** medium (the `continue`-to-`return` rewrite is the classic extraction footgun; needs
  a line-by-line review). **Effort:** medium. **Priority:** medium.

### Note - ClassifyDrift already pure and tested

`ClassifyDrift` (~L635) is already a clean pure `private static` and is tested in
`Rendering/SmoothingPipelineTests.cs`. No action.

---

## Rendering/AnchorPropagator.cs

### LOW - TryEvaluateSmoothedWorldPos is already well-factored

- **Function:** `private static Vector3d? TryEvaluateSmoothedWorldPos(...)` (~L823, ~69 lines).
- **Assessment:** has a test seam (`SmoothedPositionForTesting`), a clean OrbitalCheckpoint
  branch, and a body-resolution fallback with `surfaceLookup`. It already delegates to
  `TrajectoryMath.EvaluateOrbitSegmentAtUT`, `CatmullRomFit.Evaluate`, and
  `FrameTransform.DispatchSplineWorldByFrameTag`. There is no large cohesive block worth
  carving. Not proposed. **Priority:** low.

---

## Explicitly NOT proposed (would change behavior or restructure protected code)

1. **`GhostPlaybackEngine.UpdatePlayback` (~613 lines) one-shot decomposition.** Per-frame hot
   path with ~15 guard branches each ending in `continue`, plus shared mutable locals
   (`state`, `ghostActive`, `suppressGhosts`) read and written across branches. Any extraction
   of a guard block changes the `continue`/local-mutation interaction (item 5). The loop body
   also builds a per-frame `engineIterBuilder` and rate-limited trace. This needs a dedicated
   decomposition plan with per-branch review, not a line-item here. The narrow pass already
   takes the safe M2 preamble pair; further carving is out of scope until a plan exists.

2. **`GhostPlaybackEngine.RenderInRangeGhost` (~348 lines).** Same hot-path/interleaved-mutation
   concerns; it takes `ref state, ref ghostActive` and threads them through positioning, FX, and
   completion logic. Dedicated plan only.

3. **`BallisticExtrapolator.Extrapolate` (~346 lines) while-loop decomposition.** The loop
   carries `currentState`, `currentBody`, `soiTransitions`, and three `suppressedImmediate*`
   variables that are read and written across iterations. The ~8 "set 5 terminal fields + log +
   return" stanzas look deduplicable, but each logs a DIFFERENT message (item 9 keeps logging
   observational, but a shared "SetTerminal" helper that also logged would change which message
   fires) and several also set `failureReason`. A field-only `SetTerminal(result, state, ut,
   body, pos, vel)` helper with the log left at the call site is borderline acceptable but
   low-value given the 25 existing end-to-end tests; not proposed to avoid touching a
   well-covered hot loop. The two SOI-transition `currentState = new BallisticStateVector{...}`
   blocks (ParentExit / ChildEntry) differ in the sign of the body offset, the
   `suppressedImmediate*` assignment, and which body becomes current; unifying them would need
   enough parameters to obscure the difference. Leave as a dedicated plan.

4. **`FlightRecorder.OnPhysicsFrame` (~143 lines) and `OnPartJointBreak` (~181 lines).**
   `OnPhysicsFrame` is the per-frame recorder entry; `OnPartJointBreak` is a KSP event handler
   with interleaved decoupler/joint state. Both are event-driven with ordering-sensitive
   side effects; not safe for opportunistic carving. Dedicated review if touched.

5. **`UpdateAnchorDetection` full three-way unification.** As noted above, only the two exit-case
   tails are safely deduplicable; unifying all three (including the relative-enter seed-failure
   `return`) would bury control flow (item 4). The full unification is explicitly NOT proposed.

6. **Coroutine bodies.** Any `IEnumerator` in these files (e.g. `AdvanceTimelineGhostBuild`
   in `GhostVisualBuilder`) is off-limits for structural change per item 6 (logging-only edits
   allowed). Not proposed.

---

## How to use this inventory

Each item above is a starting point. Before implementing any one:

1. Re-locate the block by function name and description (line numbers will have drifted).
2. Write a focused proposal: exact block, helper signature, access modifier, and the
   checklist items that make it safe.
3. Confirm item 13: no pre-existing access modifier change. If `internal static` is blocked by
   a `private` parameter/return type, scale back to `private static`.
4. Implement, then dispatch a fresh-context Opus review per `docs/dev/refactor-guidelines.md`.
5. Add the unit test (for genuine pure seams) or characterization test (for test gaps) in the
   same change.
