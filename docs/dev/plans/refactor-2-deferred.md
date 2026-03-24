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

**Why deferred:** A callback-based `CommitChainSegmentCore(Action<Recording> preCommitCustomization)` changes the call pattern. Each method has unique pre/post steps:
- CommitChainSegment: EVA crew name + continuation sampling post-commit
- CommitDockUndockSegment: dock/undock PartEvent injection + `ChainBranch = 0`
- CommitBoundarySplit: SegmentPhase/SegmentBodyName tagging
- HandleVesselSwitchChainTermination: derives phase/body from vessel, does NOT null VesselSnapshot, terminates chain instead of advancing, handles continuation cleanup

The shared core would need 3-4 parameters/flags to account for these differences, making the "helper" harder to reason about than the current explicit copies.

**Revisit when:** Pass 3. If chain management is extracted to its own class (`ChainSegmentManager`), the pattern may be cleanable with an instance method that owns the shared state.

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

**What:** The sequence `PositionLoopGhost/InterpolateAndPosition → SetInterpolated → ApplyPartEvents → ApplyFlagEvents → UpdateReentryFx → RCS toggle` appears 4+ times across UpdateTimelinePlayback, UpdateLoopingTimelinePlayback, and UpdateOverlapLoopPlayback.

**Why deferred:** The blocks are similar but not identical — each has different parameters for `suppressVisualFx`, anchor-relative mode, and seed offsets. Extracting would require 5+ parameters, making the helper signature unwieldy. The blocks are also deeply embedded in loop iterations with local variables.

**Revisit when:** Pass 3. If the three playback methods move to a `TimelinePlaybackController` class, the shared pattern may be cleanable with instance state.

---

### D6. Ghost re-show after warp-down dedup

**What:** Same pattern appears 3 times: check `!ghost.activeSelf`, check `currentZone != Beyond`, `SetActive(true)`, log with `loggedReshow` dedup.

**Why deferred:** Each instance is 4-5 lines — below the 5-line minimum extraction threshold. The pattern is simple and self-documenting.

---

### D7. HandleAtmosphereBoundarySplit / HandleSoiChangeSplit near-dedup

**What:** Same structure (guard → tree suppress → non-tree commit+restart), differ in phase-name derivation and ScreenMessage text.

**Why deferred:** Each is only 36-38 lines. The differences (phase derivation, screen message, which recorder flag is checked) would require 3 parameters that make the shared method harder to read than the two explicit copies. Net savings would be ~15 lines.

---

### D8. UpdateTimelinePlayback full decomposition

**What:** 681-line method with 18+ phases. Group B extracts FlushDeferredSpawns and EvaluateGhostSoftCaps (the bookend phases), but the 500-line per-recording loop body remains.

**Why deferred:** The loop body has heavy coupling to loop-local state (`ghostActive`, `inRange`, `pastEnd`, various flags). Extracting sub-phases would require passing 10+ local variables as parameters or converting them to fields (not allowed). The `continue` statements within the loop also prevent clean extraction.

**Revisit when:** Pass 3. If timeline playback moves to its own class, the loop locals become instance fields and sub-methods become natural.

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

**Why deferred:** Differences in animation lookup, endpoint logic, cache keys, and sample point count prevent clean parameterization without introducing fragile conditionals.

**Revisit when:** Pass 3 candidate.

### D16. GhostVisualBuilder particle builder dedup

**What:** SpawnPartPuffFx/SpawnExplosionFx/BuildExplosionSmokeChild share ~300 lines of ParticleSystem configuration template.

**Why deferred:** Many differing numeric parameters make clean parameterization complex.

### D17. SampleHeatAnimation3State snapshot pattern

**What:** Uses `if (t == tempClone.transform) continue;` guard not present in other SampleXxxStates methods.

**Why deferred:** Prevents sharing SnapshotTransformStates helper without adding a parameter.

---

## Deferred from ParsekUI.cs

### D18. ParsekUI window resize drag duplication
**What:** 4 windows (Recordings, Actions, Settings, Spawn Control) + Group Popup all have identical ~15-line resize drag blocks in their `*IfOpen` methods and identical ~10-line resize handle blocks in their draw methods.
**Why deferred:** Extracting requires passing the rect, bool flag, and min dimensions through a shared helper. The IMGUI Event.current.Use() calls are order-sensitive — a shared helper could subtly break input consumption if the call order changes.
**Revisit when:** Pass 3, if IMGUI code moves to its own subsystem.

### D19. ParsekUI DrawSortableHeader / DrawSpawnSortableHeader near-duplicate
**What:** Two near-identical sort header methods operating on different enum types (SortColumn vs SpawnSortColumn).
**Why deferred:** Unifying requires a generic or interface, which changes the call pattern.

---

## Deferred from Phase 3B (structural splits attempted, correctly skipped)

### D20. TimelinePlaybackController from ParsekFlight (~2443 lines)
**What:** Extract the entire `#region Timeline Auto-Playback` section: UpdateTimelinePlayback (535 lines), UpdateLoopingTimelinePlayback, UpdateOverlapLoopPlayback, ghost spawn/destroy lifecycle, positioning methods, reentry FX, watch camera. Would reduce ParsekFlight from ~9900 to ~7500 lines.

**Why deferred:** `ghostStates` dict referenced 31 times across 8 regions. Other playback fields (`overlapGhosts`, `ghostPosEntries`, `loopPhaseOffsets`, `activeExplosions`) have similarly broad cross-region usage. Every candidate method uses multiple instance fields AND instance helpers (`Log`, `SanitizeQuaternion`, `CreateGhostSphere`). Extraction requires either:
- (a) Back-reference to ParsekFlight → circular dependency
- (b) 10+ parameters per method → changes all signatures
- (c) Shared state interface → architectural redesign

All three violate zero-logic-changes constraint.

**Revisit when:** Dedicated architectural redesign task. Would need to redesign ParsekFlight's field layout so playback state is isolated in a struct/class that can be passed to the controller. Not a refactor — a feature-level change.

**Enables if done:** D2 (commit-pattern dedup becomes instance methods), D5 (frame application dedup), D8 (UpdateTimelinePlayback decomposition via instance fields).

### D21. ChainSegmentManager from ParsekFlight (~400-500 lines)
**What:** Extract chain state (`activeChainId`, `activeChainNextIndex`, `activeChainPrevId`, `activeChainCrewName`, continuation fields) and chain methods (`CommitChainSegment`, `CommitDockUndockSegment`, `CommitBoundarySplit`, `HandleVesselSwitchChainTermination`, `HandleDockUndockCommitRestart`, continuation methods).

**Why deferred:** `activeChainId` referenced 50+ times across 5 regions (Scene Change, Split Event Detection, Update Helpers, Recording, Timeline Auto-Playback). Chain state is NOT localized to chain methods — it's read/written during recording start/stop, dock/undock handling, scene change, and tree mode logic. Same fundamental issue as D20.

**Revisit when:** Same as D20. Requires isolating chain state into a struct that can be owned by either ParsekFlight or a dedicated manager.

**Enables if done:** D2 (commit-pattern dedup — the 4 commit methods could share a core method on the manager instance).

---

## Phase 3C Remaining Tasks

### C1. SanitizeQuaternion instance wrapper removal
**What:** ParsekFlight has a 3-line instance method `SanitizeQuaternion(Quaternion q)` that just forwards to `TrajectoryMath.SanitizeQuaternion(q)`. ParsekKSC correctly calls TrajectoryMath directly. 4 call sites in ParsekFlight.
**Action:** Replace 4 call sites with `TrajectoryMath.SanitizeQuaternion(q)`, delete the wrapper.

### C2. Namespace consistency verification
**Action:** Verify all files use `namespace Parsek` (or `namespace Parsek.Patches`). Verify new files (EngineFxBuilder.cs, MaterialCleanup.cs) match.

### C3. One-class-per-file verification
**Action:** Verify every .cs file has exactly one public/internal type. Exceptions: data-type files (GhostTypes.cs, GameStateEvent.cs) that bundle related types are acceptable.

### C4. Inventory doc final update
**Action:** Mark all files Pass3-Done where applicable. Update line counts to final values.

---

## Summary

| ID | File | Lines affected | Risk | Status |
|----|------|---------------|------|--------|
| D1 | ParsekFlight | ~160 | Medium | Open — conditional flags needed |
| D2 | ParsekFlight | ~316 | High | Open — **unblocked by D20 completion** (instance methods on GhostPlaybackEngine) |
| D3 | ParsekFlight+ParsekKSC | ~120 | Low | **DONE** (Phase 3A Split 4: TrajectoryMath.InterpolatePoints) |
| D4 | ParsekFlight+ParsekKSC | ~60 | Low | **DONE** (Phase 3A Split 4: shared positioning) |
| D5 | ParsekFlight | ~80 | Medium | Open — **unblocked by D20 completion** |
| D6 | ParsekFlight | ~15 | N/A | Closed — below 5-line min |
| D7 | ParsekFlight | ~75 | Low | Open |
| D8 | GhostPlaybackEngine | ~500 | High | Open — **unblocked by D20 completion** (UpdatePlayback decomposition on engine) |
| D9 | ParsekFlight | ~194 | Low | Closed — minimal gain |
| D10 | ParsekFlight | ~60 | Medium | Closed — API divergence |
| D11 | BackgroundRecorder | ~736 | High | Open — intentional design |
| D12 | GhostPlaybackLogic | ~110 | Medium | Closed — ~47% similarity not worth it |
| D13 | GhostVisualBuilder | ~565 | High | **DONE** (Phase 3B Split 10: EngineFxBuilder) |
| D14 | GhostVisualBuilder | ~80 | Medium | Closed — interleaved state |
| D15 | GhostVisualBuilder | ~300 | Medium | Open — SampleXxxStates unification |
| D16 | GhostVisualBuilder | ~300 | Medium | Closed — too many numeric params |
| D17 | GhostVisualBuilder | ~30 | Low | Closed — guard param |
| D18 | ParsekUI | ~125 | Medium | Open |
| D19 | ParsekUI | ~40 | Low | Open |
| D20 | ParsekFlight→GhostPlaybackEngine | ~2443 | High | **DONE** (T25: GhostPlaybackEngine 1553 lines + ParsekPlaybackPolicy 192 lines + interfaces) |
| D21 | ParsekFlight | ~400-500 | High | Open — requires state isolation |
