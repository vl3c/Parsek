# Refactor-2 Deferred Items

Items identified during refactoring but deferred for safety or scope reasons. Review after Pass 1 completes — some may become Pass 3 candidates.

---

## Deferred from ParsekFlight.cs

### D1. CreateBreakupChildRecording dedup (Group A skip)

**What:** ProcessBreakupEvent has 2 loops (controlled + debris children), PromoteToTreeForBreakup has 2 similar loops. All 4 create child Recordings with snapshots.

**Why deferred:** The 4 sites diverge beyond parameterization:
- ProcessBreakupEvent does inline `BackgroundMap[pid] = childRecId` + `backgroundRecorder.OnVesselBackgrounded(pid)` + conditional `SetDebrisExpiry`
- PromoteToTreeForBreakup does NONE of those — uses bulk `RebuildBackgroundMap()` + bulk TTL setup later
- Log messages differ in prefix and fields across all 4 sites
- Extracting would require a boolean flag controlling BackgroundMap behavior, introducing conditional logic that doesn't exist at any single call site

**Revisit when:** Pass 3 restructuring. If PromoteToTreeForBreakup is split into a separate class, the shared pattern may simplify.

---

### D2. Commit-pattern dedup (CommitChainSegment / CommitDockUndockSegment / CommitBoundarySplit / HandleVesselSwitchChainTermination)

**What:** 4 methods (~316 lines) share a stash-tag-commit-advance pattern (~60% identical code).

**Status:** **DONE** (T28). Extracted `CommitSegmentCore(FlightRecorder, string, Action<Recording>, bool)` on ChainSegmentManager. All 4 commit methods now delegate to CommitSegmentCore via `Action<Recording>` callback for per-method customization. CommitSegmentCore handles nullable CaptureAtStop for boundary splits.

---

### D3. Cross-file interpolation dedup (ParsekFlight.InterpolateAndPosition / ParsekKSC.InterpolateAndPositionKsc)

**What:** Both files have nearly identical core interpolation: FindWaypointIndex → get before/after points → compute t → body lookup → GetWorldSurfacePosition → Lerp/Slerp → NaN guard. ~60 lines duplicated.

**Why deferred:** Cross-file change — not allowed in Pass 1 (same-file only). The Flight version has LateUpdate registration, InterpolationResult, and CorrectForBodyRotation that the KSC version lacks.

**Revisit when:** Pass 3. Extract core interpolation to `TrajectoryMath.InterpolatePoints(before, after, t, out pos, out rot)` with each caller wrapping with its own registration/result logic.

---

### D4. Cross-file PositionGhostAt dedup (ParsekFlight.PositionGhostAt / ParsekKSC.PositionGhostAtPoint)

**What:** Structurally identical single-point positioning. Same body lookup, GetWorldSurfacePosition, SanitizeQuaternion, apply transform pattern.

**Why deferred:** Cross-file change — Pass 3 candidate.

---

### D5. Ghost playback frame application dedup

**What:** The sequence `ApplyPartEvents → ApplyFlagEvents → UpdateReentryFx → RCS toggle` appears 4 times across UpdatePlayback, UpdateLoopingPlayback, and UpdateOverlapPlayback.

**Status:** **DONE** (PR #85). Extracted `ApplyFrameVisuals` private method with `skipPartEvents` parameter to preserve Site 1 semantics (reentry FX + RCS run unconditionally even when part events are zone-skipped). 4 call sites updated.

---

### D6. Ghost re-show after warp-down dedup

**What:** Same pattern appears 3 times: check `!ghost.activeSelf`, check `currentZone != Beyond`, `SetActive(true)`, log with `loggedReshow` dedup.

**Why deferred:** Each instance is 4-5 lines — below the 5-line minimum extraction threshold. The pattern is simple and self-documenting.

---

### D7. HandleAtmosphereBoundarySplit / HandleSoiChangeSplit near-dedup

**What:** Same structure (guard → tree suppress → non-tree commit+restart), differ in phase-name derivation and ScreenMessage text.

**Status:** **DONE** — extracted `CommitBoundaryAndRestart(phase, bodyName, logMessage, screenMessage)` shared tail. Guards, tree-mode suppression, and phase computation stay in each method.

---

### D8. UpdateTimelinePlayback full decomposition

**What:** UpdatePlayback loop body (~207 lines) with multiple phases: spawn, position, apply visuals, past-end handling.

**Status:** **DONE** (PR #85). Extracted `RenderInRangeGhost` (~84 lines, spawn + position + visuals) and `HandlePastEndGhost` (~47 lines, merged active/inactive past-end blocks). Loop body reduced from ~207 to ~70 lines. Uses `ref` params for state modified by spawn, return-bool for `continue` semantics. No instance field pollution — all loop-local state stays as locals.

---

### D9. OnFlightReady full decomposition

**What:** 194-line initialization method with ~13 phases. Many phases are already delegating to well-named methods, but the orchestration is long.

**Why deferred:** Most phases are 5-15 lines — below extraction threshold. The method is a linear initialization sequence with no duplication. Splitting it would just move the same code to methods called once, adding indirection without clarity.

---

### D10. ApplyResourceDeltas / ApplyTreeLumpSum resource clamping pattern

**What:** Both methods have 3 nearly identical blocks for funds/science/reputation: read delta, clamp against balance, log clamping, apply, log. The KSP API differs per resource type (Funding.Instance.AddFunds vs ResearchAndDevelopment.Instance.AddScience vs Reputation.Instance.AddReputation).

**Why deferred:** The different API types prevent a generic helper without reflection or delegates. A delegate-based approach would change the call pattern. Each block is ~10 lines.

---

## Deferred from Other Files (identified during audit)

### D11. BackgroundRecorder 17 Check*State methods — no generic helper

**What:** 736 lines of near-identical part-event polling methods. Same pattern but vary in module types, key types, and classification logic.

**Why deferred:** Per plan: "do NOT unify into a generic helper — same ruling as R1 FlightRecorder." Changes the call pattern.

### D12. GhostPlaybackLogic SetEngineEmission / SetRcsEmission dedup

**What:** ~50-60% identical (shared particle on/off core, different diagnostic tracking).

**Why deferred:** Audit showed only 50-60% similarity (not 80% as initially estimated). Each has unique diagnostic code paths. Forced dedup would require conditional diagnostics that don't exist in either method.

### D13. GhostVisualBuilder TryBuildEngineFX per-engine fallbacks

**What:** 565-line method with 342 lines of per-engine-name fallback blocks.

**Why deferred:** 6 local function closures capture loop-scoped variables. Extracting would require changing closures to parameters (forbidden).

**Revisit when:** Pass 3 candidate: if engine FX moves to its own class, closures become instance state.

### D14. GhostVisualBuilder BuildHeatMaterialStates inner loop

**What:** Per-material loop interleaves with renderer-level cumulative state.

**Why deferred:** Not cleanly bounded for extraction.

### D15. GhostVisualBuilder SampleXxxStates full unification

**What:** 4 methods share ~80% structure but differ in: animation lookup strategy, stowed/deployed endpoint logic, cache keys, and (for heat) sample point count. ~300 lines savings possible but risky.

**Status:** **DONE** (PR #82). Extracted `SampleAnimationStates` core method with `AnimLookup` enum + `FindAnimation` resolver. Consolidated 4 caches into 1 `animationSampleCache`. Ladder scoring via `useScoring` flag. Net -139 lines.

### D16. GhostVisualBuilder particle builder dedup

**What:** SpawnPartPuffFx/SpawnExplosionFx/BuildExplosionSmokeChild share ~300 lines of ParticleSystem configuration template.

**Why deferred:** Many differing numeric parameters make clean parameterization complex.

### D17. SampleHeatAnimation3State snapshot pattern

**What:** Uses `if (t == tempClone.transform) continue;` guard not present in other SampleXxxStates methods.

**Why deferred:** Prevents sharing SnapshotTransformStates helper without adding a parameter.

---

## Deferred from ParsekUI.cs

### D18. ParsekUI window resize drag duplication
**What:** 4 windows (Recordings, Actions, Spawn Control) + Group Popup all have identical ~15-line resize drag blocks and ~10-line resize handle blocks.
**Status:** **DONE** (T30). Extracted `HandleResizeDrag` and `DrawResizeHandle` static helpers with `ref Rect` + `ref bool` parameters. 8 call sites replaced. Group Popup passes null for windowName to suppress logging. Latent bugfix: Group Popup now gets `Event.current.Use()` on MouseDrag.

### D19. ParsekUI DrawSortableHeader / DrawSpawnSortableHeader near-duplicate
**What:** Two near-identical sort header methods operating on different enum types (SortColumn vs SpawnSortColumn).
**Status:** **DONE**. Extracted `DrawSortableHeaderCore<TCol>` with `ref TCol currentCol, ref bool ascending, Action onChanged`. Both wrappers delegate to the core. `ToggleSpawnSort` removed (absorbed into core). All 11 call sites unchanged.

---

## Deferred from Phase 3B (structural splits attempted, correctly skipped)

### D20. TimelinePlaybackController from ParsekFlight (~2443 lines) — DONE
**What:** Extract the entire `#region Timeline Auto-Playback` section: UpdateTimelinePlayback, UpdateLoopingTimelinePlayback, UpdateOverlapLoopPlayback, ghost spawn/destroy lifecycle, positioning methods, reentry FX, watch camera.

**Completed in T25:** GhostPlaybackEngine (1553 lines) + ParsekPlaybackPolicy (192 lines) + IPlaybackTrajectory + IGhostPositioner + GhostPlaybackEvents interfaces. Solved via interface-based decomposition: engine accesses trajectories through IPlaybackTrajectory (no Recording dependency), delegates positioning to IGhostPositioner (no scene dependency), and fires lifecycle events for policy layer. ParsekFlight reduced from ~9900 to ~8657 lines.

**Enabled:** D2 (commit-pattern dedup via instance methods), D5 (frame application dedup), D8 (UpdatePlayback decomposition on engine).

### D21. ChainSegmentManager from ParsekFlight (~400-500 lines)
**What:** Extract chain state and chain methods into a ChainSegmentManager class.

**Status:** **DONE** (T26 Phase 1 + Phase 2).
- Phase 1: 16 chain state fields moved to ChainSegmentManager. ~150 field accesses migrated.
- Phase 2: 12 methods (~505 lines) moved. Group A (8 pure continuation methods) moved directly. Group B (4 commit methods) refactored: receive recorder as parameter, return bool for abort handling. `CommitVesselSwitchTermination` split from `HandleVesselSwitchChainTermination` (guards + recorder=null stay as thin wrapper on ParsekFlight).
- 3 orchestration methods stay on ParsekFlight (HandleDockUndockCommitRestart, HandleChainBoardingTransition, CommitBoundaryAndRestart — own StartRecording lifecycle).
- ChainSegmentManager: 686 lines. ParsekFlight net -620 lines.

**Enabled:** D2 (CommitSegmentCore extracted, T28 done).

---

## Phase 3C Remaining Tasks

### C1. SanitizeQuaternion instance wrapper removal
**What:** ParsekFlight has a 3-line instance method `SanitizeQuaternion(Quaternion q)` that just forwards to `TrajectoryMath.SanitizeQuaternion(q)`. ParsekKSC correctly calls TrajectoryMath directly. 4 call sites in ParsekFlight.
**Status:** **DONE** — wrapper was already removed during refactor-2. All call sites already use `TrajectoryMath.SanitizeQuaternion` directly.

### C2. Namespace consistency verification
**Action:** Verify all files use `namespace Parsek` (or `namespace Parsek.Patches`). Verify new files (EngineFxBuilder.cs, MaterialCleanup.cs) match.

### C3. One-class-per-file verification
**Action:** Verify every .cs file has exactly one public/internal type. Exceptions: data-type files (GhostTypes.cs, GameStateEvent.cs) that bundle related types are acceptable.

### C4. Inventory doc final update
**Status:** **DONE** — Line counts updated for all modified files. ParsekFlight 8098, GhostPlaybackEngine 1594, ChainSegmentManager 686 (new), ParsekUI 3557, GhostPlaybackLogic 2274, GhostPlaybackState 75, GhostChain 53.

---

## Summary

| ID | File | Lines affected | Risk | Status |
|----|------|---------------|------|--------|
| D1 | ParsekFlight | ~160 | Medium | Open — conditional flags needed |
| D2 | ChainSegmentManager | ~316 | High | **DONE** (T28: CommitSegmentCore + per-method customization callbacks) |
| D3 | ParsekFlight+ParsekKSC | ~120 | Low | **DONE** (Phase 3A Split 4: TrajectoryMath.InterpolatePoints) |
| D4 | ParsekFlight+ParsekKSC | ~60 | Low | **DONE** (Phase 3A Split 4: shared positioning) |
| D5 | GhostPlaybackEngine | ~80 | Medium | **DONE** (PR #85: ApplyFrameVisuals with skipPartEvents param) |
| D6 | ParsekFlight | ~15 | N/A | Closed — below 5-line min |
| D7 | ParsekFlight | ~75 | Low | **DONE** (CommitBoundaryAndRestart shared tail) |
| D8 | GhostPlaybackEngine | ~500 | High | **DONE** (PR #85: RenderInRangeGhost + HandlePastEndGhost, loop body 207→70 lines) |
| D9 | ParsekFlight | ~194 | Low | Closed — minimal gain |
| D10 | ParsekFlight | ~60 | Medium | Closed — API divergence |
| D11 | BackgroundRecorder | ~736 | High | Open — intentional design |
| D12 | GhostPlaybackLogic | ~110 | Medium | Closed — ~47% similarity not worth it |
| D13 | GhostVisualBuilder | ~565 | High | **DONE** (Phase 3B Split 10: EngineFxBuilder) |
| D14 | GhostVisualBuilder | ~80 | Medium | Closed — interleaved state |
| D15 | GhostVisualBuilder | ~300 | Medium | **DONE** (PR #82: SampleAnimationStates + AnimLookup) |
| D16 | GhostVisualBuilder | ~300 | Medium | Closed — too many numeric params |
| D17 | GhostVisualBuilder | ~30 | Low | Closed — guard param |
| D18 | ParsekUI | ~125 | Medium | **DONE** (T30: HandleResizeDrag + DrawResizeHandle helpers) |
| D19 | ParsekUI | ~40 | Low | **DONE** (DrawSortableHeaderCore<TCol> generic extraction) |
| D20 | ParsekFlight→GhostPlaybackEngine | ~2443 | High | **DONE** (T25: GhostPlaybackEngine 1553 lines + ParsekPlaybackPolicy 192 lines + interfaces) |
| D21 | ParsekFlight→ChainSegmentManager | ~400-500 | High | **DONE** (Phase 1: state isolation. Phase 2: 12 methods moved. CommitSegmentCore extracted. ParsekFlight -620 lines.) |
