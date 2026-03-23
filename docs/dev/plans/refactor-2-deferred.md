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

---

## Summary

| ID | File | Lines affected | Risk if attempted | Pass 3 candidate? |
|----|------|---------------|-------------------|-------------------|
| D1 | ParsekFlight | ~160 | Medium (conditional flags) | Yes |
| D2 | ParsekFlight | ~316 | High (callback pattern) | Yes |
| D3 | ParsekFlight+ParsekKSC | ~120 | Low (pure extraction) | Yes |
| D4 | ParsekFlight+ParsekKSC | ~60 | Low (pure extraction) | Yes |
| D5 | ParsekFlight | ~80 | Medium (many params) | Yes |
| D6 | ParsekFlight | ~15 | N/A (below 5-line min) | No |
| D7 | ParsekFlight | ~75 | Low | Maybe |
| D8 | ParsekFlight | ~500 | High (loop coupling) | Yes |
| D9 | ParsekFlight | ~194 | Low (but minimal gain) | No |
| D10 | ParsekFlight | ~60 | Medium (API divergence) | No |
| D11 | BackgroundRecorder | ~736 | High (call pattern) | Maybe |
| D12 | GhostPlaybackLogic | ~110 | Medium (low similarity) | Maybe |
