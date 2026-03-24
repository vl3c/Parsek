# Parsek Refactor-2 Architecture Analysis (Pass 2)

**Read-only analysis. No code changes. Produced after Pass 1 completion.**

---

## 1. Dependency Graph — Top Coupling

| File | Dependencies | Count |
|------|-------------|-------|
| ParsekFlight.cs | ~50 Parsek files | **50** |
| ParsekUI.cs | 24 | **24** |
| FlightRecorder.cs | 22 | **22** |
| RecordingStore.cs | 18 | **18** |
| ParsekScenario.cs | 18 | **18** |
| BackgroundRecorder.cs | 15 | **15** |
| MergeDialog.cs | 12 | 12 |
| GhostPlaybackLogic.cs | 11 | 11 |
| VesselGhoster.cs | 11 | 11 |
| Recording.cs | 10 | 10 |
| VesselSpawner.cs | 10 | 10 |

Files with >8 dependencies flagged as high coupling. ParsekFlight depends on virtually every other file in the project.

---

## 2. Static Mutable State — Key Cross-File Mutations

| Field | Declared In | Written By (external) |
|-------|-------------|----------------------|
| GameStateRecorder.SuppressCrewEvents | GameStateRecorder | ActionReplay, ParsekScenario |
| GameStateRecorder.SuppressResourceEvents | GameStateRecorder | ParsekScenario, ParsekFlight |
| RecordingStore.IsRewinding | RecordingStore | ParsekScenario (ParsekFlight reads only) |
| RecordingStore.RewindUT/AdjustedUT | RecordingStore | ParsekScenario (ParsekFlight reads only) |
| RecordingStore.pendingRecording | RecordingStore | ParsekFlight |
| RecordingStore.pendingTree | RecordingStore | ParsekFlight, MergeDialog |
| PhysicsFramePatch.ActiveRecorder | PhysicsFramePatch | FlightRecorder |
| PhysicsFramePatch.BackgroundRecorderInstance | PhysicsFramePatch | ParsekFlight |
| FlightResultsPatch.Bypass | FlightResultsPatch | MergeDialog (indirect: via ReplayFlightResults) |
| MilestoneStore.CurrentEpoch | MilestoneStore | ParsekScenario |
| ResourceBudget.budgetDirty | ResourceBudget | MilestoneStore |

---

## 3. Nested Types Used Externally (Extraction Candidates)

| Parent | Nested Type | Used By |
|--------|------------|---------|
| ResourceBudget | BudgetSummary | RecordingStore, ParsekScenario |
| FlightRecorder | VesselSwitchDecision | (FlightRecorder only + tests) |
| ParsekUI | UIMode | ParsekKSC |

---

## 4. Cross-File Duplication (Top Findings)

### 4.1 ParsekKSC.PopulateGhostInfoDictionaries — Bug-Risk Duplicate
KSC has 86-line private copy that's 90% identical to GhostPlaybackLogic's shared version. **KSC version misses heat cold-state initialization.** This is a latent bug — KSC ghosts don't start heat-animated parts in cold state.

### 4.2 ParsekKSC ↔ ParsekFlight Constants
`DefaultLoopIntervalSeconds`, `MinLoopDurationSeconds`, `GetLoopIntervalSeconds` duplicated. Should live in GhostPlaybackLogic.

### 4.3 ParsekKSC ↔ ParsekFlight Interpolation
`InterpolateAndPositionKsc` (72 lines) ≈ `InterpolateAndPosition` (78 lines), ~75% shared. Core math belongs in TrajectoryMath.

### 4.4 BackgroundRecorder ↔ FlightRecorder Part Polling
17 Check*State method pairs, ~70-80% shared. But Layer 1 (pure transition logic) is already shared. Layer 2 duplication is intentional design (per-vessel state isolation).

### 4.5 Engine/RCS FX
SetEngineEmission ↔ SetRcsEmission: confirmed ~47% shared. Not worth unifying (per deferred doc D12).

---

## 5. Dead Code + Unnecessary Indirection

- `GhostVisualBuilder.GetFairingShowMesh` — zero call sites (production or test). **Dead code.**
- `GhostVisualBuilder.GenerateFairingTrussMesh` — zero call sites. **Dead code.**
- `ParsekFlight.SanitizeQuaternion` instance wrapper — has 4 call sites within ParsekFlight, but is an unnecessary indirection over `TrajectoryMath.SanitizeQuaternion` (which ParsekKSC calls directly). **Not dead code** — unnecessary wrapper. Phase 3C cleanup candidate.

---

## 6. Concrete Split Recommendations (Pass 3 Execution Order)

### Phase 3A — Low-Risk Quick Wins

| # | Split | From | Action | Lines | Risk |
|---|-------|------|--------|-------|------|
| 1 | KSC PopulateGhostInfoDictionaries | ParsekKSC | Delete private copy, call GhostPlaybackLogic shared version | -86 | Low |
| 2 | Loop constants → GhostPlaybackLogic | ParsekKSC + ParsekFlight | Move constants + GetLoopIntervalSeconds | ~20 | Low |
| 3 | KSC StopParticleSystems → GhostPlaybackLogic | ParsekKSC | New StopAllEngineFx/StopAllRcsFx methods | -38 | Low |
| 4 | Ghost positioning → TrajectoryMath | ParsekFlight + ParsekKSC | Shared InterpolateAndApply + PositionFromPoint | ~70 saved | Medium |

### Phase 3A also — Nested Type Extractions

| # | Type | From | To |
|---|------|------|----|
| 5 | BudgetSummary | ResourceBudget (nested) | Top-level in ResourceBudget.cs (only 2 external users) |
| 6 | VesselSwitchDecision | FlightRecorder (nested) | Low priority (no external production users) |
| 7 | UIMode | ParsekUI (nested) | Own file |
| 8 | MaterialCleanup | GhostVisualBuilder (nested) | Own file |
| 9 | Dead code removal | GhostVisualBuilder | Delete GetFairingShowMesh, GenerateFairingTrussMesh |

### Phase 3B — Structural Splits (High Impact)

| # | Split | Lines Moved | Risk | Enables |
|---|-------|-------------|------|---------|
| 10 | EngineFxBuilder from GhostVisualBuilder | ~600 | Medium | D13 |
| 11 | TimelinePlaybackController from ParsekFlight | ~2443 | High | D2, D5, D8 |
| 12 | ChainSegmentManager from ParsekFlight | ~400-500 | High | D2 |

### Phase 3C — Cleanup

- Verify one class per file
- Verify namespace consistency
- Remove ParsekFlight.SanitizeQuaternion instance wrapper (dead code)
- Final build + test

---

## Cross-Reference Summary (for Pass 3 planning)

FlightRecorder is the most cross-referenced file: its `internal static` methods are called by BackgroundRecorder (25+), PartStateSeeder (10+), GhostVisualBuilder (2), ParsekFlight (15+), and 20+ test files. Moving any FlightRecorder method requires updating 3-5 production files + 5-10 test files.

GhostVisualBuilder methods are mostly self-contained (internal calls). External callers: ParsekFlight, ParsekKSC, GhostPlaybackLogic.

RecordingStore serialize/deserialize methods are called by ParsekScenario (production) and 15+ test files.

Full cross-reference map available in the analysis agent output.
