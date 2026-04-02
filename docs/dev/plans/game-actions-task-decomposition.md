# Game Actions & Resource System — Task Decomposition

Task breakdown for implementing `docs/parsek-game-actions-system-design.md`. Follows the ordering from `docs/dev/redesign-task-template.md`: data model → core mechanism → modules → integration → polish.

**Design doc:** `docs/parsek-game-actions-system-design.md` (1967 lines, 12 sections + appendix)
**Inventory:** `docs/dev/plans/game-actions-inventory.md`
**Deferred items:** `docs/dev/plans/game-actions-deferred.md`

---

## Phase 0: Risk Reduction Spikes

Before starting Phase 1, run four focused spikes to confirm or kill the hardest assumptions. Each spike is a time-boxed investigation (not implementation) that produces findings, not code.

### Spike A: Reputation Curve Extraction

Decompile `Reputation.AddReputation()` from Assembly-CSharp.dll to extract the exact gain/loss curve formula. This blocks the reputation module (Task 9, deferred item D1). Determine: is the curve a simple polynomial, lookup table, or AnimationCurve? Can it be replicated with a pure function?

**If infeasible:** Use a linear approximation (multiplier = 1.0 at all rep levels) as a placeholder. Task 9 ships with approximate rep and a TODO to swap in the real curve. Acceptable because rep doesn't gate any hard constraint (unlike science/funds).

### Spike B: Contract State Patching Feasibility

Prototype `ContractSystem` state manipulation: can Parsek reliably add/remove/complete contracts programmatically? Test `Contract.Load(ConfigNode)` from a previously captured snapshot. Determine: how much state can be round-tripped? Do contract parameters survive? This informs Task 15b and deferred item D3.

**If infeasible:** Descope Task 15b's contract patching to event-only tracking (no state reconstruction). Contracts are observed and logged but not restored after rewind — KSP's procedural generation handles re-offering. Slot reservation still works (computed from ledger, not ContractSystem).

### Spike C: Kerbal Roster Manipulation

Test programmatic kerbal roster operations beyond what `CrewReservationManager` already does: add kerbals with specific XP levels, set experience trait, place in retired/unassigned state, verify dismissal protection. Determine: can Parsek control all aspects needed by the Kerbals module (Tasks 10a/b)?

**If infeasible:** Identify which specific operations fail and redesign around them. Most operations are already used in existing code (3 callsites for GetNewKerbal, 2 for SetExperienceTrait), so total infeasibility is unlikely — individual operations may need workarounds.

### Spike D: KSC Event Hooks

Investigate how KSP reports KSC-time events (tech unlock, facility upgrade, kerbal hire) — specifically which `GameEvents` fire and in what order. This de-risks Task 18 (KSC Spending Actions), which is in Phase 6 but involves API surface that could surprise.

**If infeasible:** Fall back to polling-based detection (same approach already used for facility upgrades). More complex but proven. Alternatively, defer Task 18 and rely on existing ActionReplay for KSC actions.

---

## Phase 1: Foundation

### Task 1: Core Data Types and Action Schemas

**Overview:** Define all action schema types from the design doc as C# types. No behavior, no I/O — just types and serialization round-trips.

**New file:** `Source/Parsek/GameAction.cs` (~350 lines)

Types to create:
- `GameActionType` enum — `ScienceEarning`, `ScienceSpending`, `FundsEarning`, `FundsSpending`, `MilestoneAchievement`, `ContractAccept`, `ContractComplete`, `ContractFail`, `ContractCancel`, `ReputationEarning`, `ReputationPenalty`, `KerbalAssignment`, `KerbalHire`, `KerbalRescue`, `KerbalStandIn`, `FacilityUpgrade`, `FacilityDestruction`, `FacilityRepair`, `StrategyActivate`, `StrategyDeactivate`, `FundsInitial`
- `ScienceMethod` enum — `Transmitted`, `Recovered`
- `FundsSource` enum — `ContractComplete`, `ContractAdvance`, `Recovery`, `Milestone`, `Other`
- `FundsSpendingSource` enum — `VesselBuild`, `FacilityUpgrade`, `FacilityRepair`, `KerbalHire`, `ContractPenalty`, `Strategy`, `Other`
- `ReputationSource` enum — `ContractComplete`, `Milestone`, `Other`
- `ReputationPenaltySource` enum — `ContractFail`, `ContractDecline`, `KerbalDeath`, `Strategy`, `Other`
- `KerbalEndState` enum — `Recovered`, `Dead`, `MIA`, `Stranded`
- `GameAction` class — base with `ut`, `type`, `recordingId` (nullable), `sequence`
- Derived types or fields for each action schema (section 4.3, 4.4, 5.5, 6.4, 7.2, 8.4, 9.8, 10.2, 11.2)

**Serialization:** ConfigNode round-trip. Each action serializes to a `GAME_ACTION` ConfigNode with a `type` discriminator and schema-specific fields. Use `ToString("R", InvariantCulture)` for all floats/doubles.

**Tests:** `GameActionSerializationTests.cs` — round-trip every action type, verify all fields survive save/load. ~20 tests.

**Depends on:** Nothing.
**Enables:** Tasks 2-12.
**Done when:** All types compile, serialization round-trips pass.

---

### Task 2: Ledger File I/O

**Overview:** Implement the ledger file — persistence layer for all game actions. Follows the safe-write pattern from `RecordingStore`. Single file `saves/<save>/parsek/ledger.dat` containing all actions.

**New file:** `Source/Parsek/Ledger.cs` (~400 lines)

Functionality:
- `SaveToFile(string path, List<GameAction> actions)` — safe-write (.tmp + rename)
- `LoadFromFile(string path) → List<GameAction>` — parse all actions from file
- `Reconcile(List<GameAction> actions, HashSet<string> validRecordingIds, double maxUT)` — prune orphaned earnings and future spendings (design doc 2.3). Note: "after" means `> maxUT` (strictly after), not `>=`.
- Path utilities: use `RecordingPaths.EnsureGameStateDirectory()` and `ResolveSaveScopedPath("Parsek/GameState/ledger.pgld")`
- **Initial seeding:** On first init for a career save, extract starting funds from save file and write `FundsInitial` action (design doc 2.5). Seeded once, immutable thereafter.

**Integration with ParsekScenario:** Add `ledgerPath` field to ScenarioModule save/load. Reference-only in .sfs, bulk data in external file.

**Tests:** `LedgerIOTests.cs` — save/load round-trip, reconciliation (prune orphaned, prune future), empty file, corrupt file handling. ~15 tests.

**Depends on:** Task 1 (action types).
**Enables:** Task 3.
**Done when:** Ledger file saves, loads, and reconciles correctly.

---

### Task 3: Recalculation Engine

**Overview:** The core recalculation walk from design doc section 1.8. Sorts all actions by (UT, type, sequence), dispatches to module handlers, produces derived state.

**New file:** `Source/Parsek/RecalculationEngine.cs` (~350 lines)

Functionality:
- `Recalculate(List<GameAction> actions) → RecalculationResult` — the unified walk
- Sort: UT ascending, earnings before spendings, then sequence
- Module interface: `IResourceModule` with `Reset()`, `ProcessAction(GameAction)`, `GetDerivedState()`
- Tier ordering: first-tier modules (Science, Milestones, Contracts, Kerbals) → strategy transform → second-tier modules (Funds, Reputation)
- `RecalculationResult` — aggregated derived state from all modules

The engine is pure computation — no KSP mutation, no file I/O.

**Tests:** `RecalculationEngineTests.cs` — sort order verification, tier ordering, empty walk, single-action walk. ~10 tests.

**Depends on:** Tasks 1-2.
**Enables:** Tasks 4-12 (modules register as handlers).
**Done when:** Engine sorts, dispatches, and produces results for empty and trivial action lists.

---

## Phase 2: Simple Modules (First-Tier)

### Task 4: Science Earnings Module

**Overview:** Science subject tracking, hard-cap walk, effective science computation. Design doc sections 4.1-4.3, 4.6.

**New file:** `Source/Parsek/ScienceModule.cs` (~200 lines)

Implements `IResourceModule`:
- `Reset()`: clear per-subject credited totals and running science
- `ProcessEarning(ScienceEarning)`: headroom = maxValue - creditedTotal, effectiveScience = min(awarded, headroom), update credited
- Per-subject state: `Dictionary<string, SubjectState>` with `creditedTotal` and `maxValue`
- Running science balance

**Tests:** `ScienceModuleTests.cs` — basic earning, retroactive priority (design doc 4.7 scenarios), cap hit, multiple collections within one recording, mixed transmit/recover. Include log assertion tests for cap-hit decisions and priority shifts. ~15 tests.

**Depends on:** Task 3 (engine interface).
**Enables:** Task 5 (science spending).
**Done when:** All verified scenarios from design doc 4.7 pass.

---

### Task 5: Science Spending and Reservation

**Overview:** Tech node unlock spending, science reservation system. Design doc sections 4.4-4.5.

**Extends:** `ScienceModule.cs` (~+100 lines)

Functionality:
- `ProcessSpending(ScienceSpending)`: check affordability, deduct from running science
- Reservation: `availableScience(ut) = sum(effective earnings up to ut) - sum(ALL spendings)`
- `GetAvailableScience(double ut)` for commit-time and KSC-time checks

**Tests:** Reservation blocks new spending (design doc 4.5 scenario), interleaved earnings/spendings (design doc 4.7 scenario 3). ~8 tests.

**Depends on:** Task 4.
**Enables:** Task 8 (funds uses same reservation pattern).
**Done when:** Reservation scenarios pass, spending blocked correctly.

---

### Task 6: Milestones Module

**Overview:** Once-ever flags, fund/rep flow to second-tier. Design doc section 7.

**New file:** `Source/Parsek/MilestonesModule.cs` (~120 lines)

Implements `IResourceModule`:
- Binary cap: each milestoneId credited exactly once
- Chronologically first recording gets `effective = true`
- `fundsAwarded` and `repAwarded` flow to Funds and Reputation modules when effective

**Tests:** `MilestonesModuleTests.cs` — basic achievement, retroactive priority shift (design doc 7.4), multiple milestones across recordings. ~8 tests.

**Depends on:** Task 3.
**Enables:** Task 8 (funds reads milestone effective flags).
**Done when:** All verified scenarios from design doc 7.4 pass.

---

### Task 7: Contracts Module

**Overview:** State machine, slot reservation (UT=0), once-ever completion, deadline generation. Design doc section 8. This is a complex first-tier module.

**New file:** `Source/Parsek/ContractsModule.cs` (~350 lines)

Functionality:
- Contract state tracking: `activeContracts` map, `creditedContracts` set
- Accept: slot consumed from UT=0
- Complete: once-ever (effective flag), rewards flow to Funds/Rep/Science
- Fail/Cancel: penalties flow, slot freed
- Deadline handling: walk generates derived `ContractFail` at deadline UT
- `availableSlots(ut)` for slot reservation check

**Tests:** `ContractsModuleTests.cs` — basic accept/complete, once-ever with retroactive priority (design doc 8.10), slot reservation across rewind, deadline failure generation. ~12 tests.

**Depends on:** Task 3.
**Enables:** Task 8 (contract fund/rep values), Task 9 (contract rep values).
**Done when:** All verified scenarios from design doc 8.10 pass.

---

## Phase 3: Second-Tier Modules

### Task 8: Funds Module

**Overview:** Seeded balance, all earning sources, spendings, reservation system. Design doc section 5.

**New file:** `Source/Parsek/FundsModule.cs` (~250 lines)

Implements `IResourceModule` (second-tier):
- `initialFunds` seed from save file
- Running balance: seed + earnings - spendings
- Earnings: milestone funds (check effective flag), contract funds, vessel recovery, other
- Spendings: vessel build, facility upgrade/repair, kerbal hire, contract penalties
- Reservation: `availableFunds(ut) = seed + earnings up to ut - ALL spendings`
- Defensive `affordable` flag on spendings

**Tests:** `FundsModuleTests.cs` — seeded balance, reservation blocks overspending (design doc 5.12), interleaved facility/hire spendings, new recording expands budget. ~12 tests.

**Depends on:** Tasks 5 (reservation pattern), 6 (milestone effective funds), 7 (contract funds), 10b (kerbal hire costs).
**Enables:** Task 15 (patching).
**Done when:** All verified scenarios from design doc 5.12 pass.

---

### Task 9: Reputation Module

**Overview:** Curve simulation, nominal→effective conversion. Design doc section 6.

**New file:** `Source/Parsek/ReputationModule.cs` (~200 lines)

Implements `IResourceModule` (second-tier):
- Running rep starting at 0
- `applyGainCurve(nominal, currentRep)` — replicate KSP's gain curve
- `applyLossCurve(nominal, currentRep)` — replicate KSP's loss curve
- Reads effective flags from milestones and contracts
- Order-dependent totals (expected, documented in design doc 6.8)

**Note:** Requires decompilation investigation for exact curve (D1). Initial implementation can use a reasonable approximation; exact formula is pluggable.

**Tests:** `ReputationModuleTests.cs` — basic gain, retroactive reordering (design doc 6.7), penalty application, curve diminishing at high rep. ~8 tests.

**Depends on:** Tasks 6, 7 (milestone/contract effective rep values).
**Enables:** Task 15 (patching).
**Done when:** Verified scenario from design doc 6.7 passes (with approximate curve).

---

## Phase 4: Complex Modules

### Task 10a: Kerbals Module — Reservation and Chains

**Overview:** Core kerbal reservation and replacement chain system. Design doc section 9.1-9.7.

**Extends:** `CrewReservationManager.cs` and/or new `KerbalsModule.cs` (~300 lines)

Functionality:
- UT=0 reservation per kerbal (continuous block, no gaps)
- End states: RECOVERED (temporary), STRANDED (open-ended), DEAD/MIA (permanent)
- Replacement chains: per-slot, ordered by generation
- Stand-in generation: same class, randomized attributes, 0 XP, free
- Retired pool: displaced stand-ins that were used in recordings
- Dismissal protection: Parsek-managed kerbals cannot be dismissed

**Tests:** `KerbalsModuleTests.cs` — simple reservation (9.11 scenario 1), deep chain (scenario 2), stranded→rescued (scenario 3), rewind recomputes (scenario 4), stand-in dies within chain (scenario 6), permanent loss (scenario 7). ~15 tests.

**Depends on:** Task 3.
**Enables:** Task 10b.
**Done when:** Chain scenarios from design doc 9.11 pass.

---

### Task 10b: Kerbals Module — XP, Hiring, Rescue

**Overview:** XP accumulation, hiring as funds spending, rescue mechanics. Design doc sections 9.8-9.13.

**Extends:** `KerbalsModule.cs` (~+150 lines)

Functionality:
- XP accumulation: walk assignments per kerbal, sum xpGained (banks on recovery only)
- Hiring: `KerbalHire` spending action, funds cost (career mode)
- Rescue: `KerbalRescue` closes stranded reservation
- Existence timeline: default crew from UT=0, hired from hire UT, rescued from recovery UT
- Astronaut Complex capacity: stand-ins and retired bypass the cap

**Tests:** Multi-crew mission (scenario 5), XP walk, hiring cost flow to funds. ~8 tests.

**Depends on:** Task 10a.
**Enables:** Task 8 (hire costs flow into funds module), Task 15 (roster patching).
**Done when:** XP and hiring scenarios pass.

**Note:** Kerbals is a first-tier module (design doc 1.8). `KerbalHire` spending actions are *produced* by this module and *consumed* by the Funds module. The dependency flows Kerbals→Funds, not Funds→Kerbals.

---

### Task 11: Facilities Module

**Overview:** Upgrade/destroy/repair lifecycle, visual state during warp. Design doc section 10.

**New file:** `Source/Parsek/FacilitiesModule.cs` (~200 lines)

Functionality:
- Action processing: upgrade (spending), destruction (recording-associated), repair (spending)
- Derived facility level at any UT: walk actions to determine level/destroyed state
- Visual state management: real-time update during warp (like ghost playback)
- Funds flow: upgrade and repair costs into Funds module

**Tests:** `FacilitiesModuleTests.cs` — upgrade sequence, destruction→repair window, funds accounting. ~8 tests.

**Depends on:** Tasks 3, 8 (facility costs are fund spendings).
**Enables:** Task 15 (facility level patching), Task 16 (visual state during warp).
**Done when:** Facility state derivable from action walk, costs flow to funds correctly.

---

### Task 12: Strategies Module

**Overview:** Time-windowed transforms on contract rewards. Design doc section 11. Lowest priority module.

**New file:** `Source/Parsek/StrategiesModule.cs` (~250 lines)

Functionality:
- Active window tracking: activate/deactivate actions
- UT=0 slot reservation (like contracts and kerbals)
- Transform application: during walk, between first-tier resolution and second-tier crediting
- Only transforms contract rewards (not milestones, not science, not recovery)
- Setup cost: spending in source resource on activation

**Tests:** `StrategiesModuleTests.cs` — basic transform (design doc 11.6 scenario 1), retroactive commit outside window (scenario 2), reservation blocks new strategy (scenario 3). ~8 tests.

**Depends on:** Tasks 7 (contract effective flags), 8 (funds), 9 (reputation).
**Enables:** None (final module).
**Done when:** All verified scenarios from design doc 11.6 pass.

---

## Phase 5: Integration

### Task 13: Flight Event Capture

**Overview:** Extend `GameStateRecorder` to capture ledger-relevant events during flight, writing rich data to the recording sidecar.

**Modifies:** `GameStateRecorder.cs`, `FlightRecorder.cs`

Functionality:
- Science: already captured (`OnScienceReceived`) — extend to capture full schema fields (`subjectId`, `scienceAwarded`, `method`, `transmitScalar`, `subjectMaxValue`)
- Milestones: subscribe to `GameEvents.OnProgressComplete`, capture `milestoneId`, `fundsAwarded`, `repAwarded`
- Contracts: subscribe to contract completion events, capture `contractId`, rewards
- Vessel cost: capture at launch (already exists as resource delta, formalize)
- Recovery: capture `OnVesselRecoveryProcessing` recovery value
- Kerbal end states: capture death (`onCrewKilled`), EVA, recovery status

**Tests:** Event capture logging tests — verify correct log lines for each captured event type. ~10 tests.

**Depends on:** Task 1 (action schema types).
**Enables:** Task 14 (commit extraction reads sidecar data).
**Done when:** All relevant KSP events are captured and written to sidecar.

---

### Task 14: Commit Path — Sidecar to Ledger Extraction

**Overview:** When a recording is committed, extract earning actions from the sidecar and append to the ledger. Design doc section 2.2.

**Modifies:** `ParsekFlight.cs` (commit path), `Ledger.cs`

Functionality:
- On commit: read sidecar, extract schema fields, create `GameAction` entries
- Append to ledger file
- Trigger recalculation
- Vessel build cost: create `FundsSpending` with `VesselBuild` source at launch UT
- Block commit if resources insufficient (reservation check)

**Tests:** `CommitExtractionTests.cs` — extract science earnings from mock sidecar, extract milestone, extract vessel cost. ~10 tests.

**Depends on:** Tasks 2 (ledger I/O), 13 (captured data in sidecar).
**Enables:** Task 15 (recalculation produces derived state to patch).
**Done when:** Commit adds earning actions to ledger and triggers recalculation.

---

### Task 15a: KSP State Patching — Resources (Science, Funds, Reputation)

**Overview:** Write derived resource state into KSP singletons after recalculation. These are well-understood APIs already used by `ResourceApplicator`.

**New file:** `Source/Parsek/KspStatePatcher.cs` (~200 lines initial)

Functionality:
- Science: `ResearchAndDevelopment.Instance` — per-subject totals, available science balance, tech tree unlock state
- Funds: `Funding.Instance.Funds` — set to `availableFunds(currentUT)`
- Reputation: `Reputation.Instance.reputation` — set to `runningRep`
- Use `SuppressResourceEvents` AND `IsReplayingActions` during patching (both flags needed — the former prevents event re-capture, the latter bypasses Harmony blocking patches like `TechResearchPatch`)
- Patch triggers: commit, rewind, warp exit, KSP load

**Tests:** Derived state computation verification (pure math, testable without KSP). ~5 tests.

**Depends on:** Tasks 4-9 (resource modules produce derived state).
**Enables:** Task 15b, Tasks 16-17.
**Done when:** Resource patching calls are structured and derived state is correct.

---

### Task 15b: KSP State Patching — Milestones, Kerbals, Facilities

**Overview:** Write derived non-resource state into KSP singletons. These APIs are less explored and may need investigation.

**Extends:** `KspStatePatcher.cs` (~+150 lines)

Functionality:
- Milestones: `ProgressTracking.Instance` — set achieved flags (needs API investigation)
- Kerbals: `HighLogic.CurrentGame.CrewRoster` — roster entries, XP, status
- Facilities: `ScenarioUpgradeableFacilities` — building levels (already known from `ActionReplay`)
- Address Harmony patch transition: `ScienceSubjectPatch` may become redundant (ledger patches per-subject totals directly); `TechResearchPatch` and `FacilityUpgradePatch` blocking logic should be replaced by ledger reservation checks

**Tests:** Manual in-game testing for KSP API interactions. ~3 unit tests for derived state.

**Depends on:** Tasks 10-12, 15a.
**Enables:** Tasks 16, 17.
**Done when:** All KSP singletons receive correct derived state.

---

### Task 16: Warp Cycle Integration

**Overview:** Event queue during warp, visual updates (facility state), recalculation on exit. Design doc section 3.3.

**Modifies:** `ParsekFlight.cs`, `FacilitiesModule.cs`

Functionality:
- During warp: collect game action UTs crossed, update facility visuals in real-time
- On warp exit: full recalculation from UT=0, patch all KSP state
- Single recalculation per warp session (not per event)
- Player feedback summary on exit ("During warp: N recordings completed, X science earned")

**Depends on:** Tasks 11, 15.
**Enables:** Task 17 (polish).
**Done when:** Warp exit triggers recalculation and patching.

---

### Task 17: Rewind Integration

**Overview:** Post-quickload recalculation and patching. Design doc section 3.4.

**Modifies:** `ParsekScenario.cs`, `ParsekFlight.cs`

Functionality:
- After KSP loads quicksave: full recalculation, patch KSP state
- KSP load: reconcile ledger (prune), recalculate, patch
- Existing rewind path already resets resources (`ResourceApplicator.CorrectToBaseline`) — replace with ledger-based recalculation

**Depends on:** Tasks 2, 15.
**Enables:** None (integration complete).
**Done when:** Rewind produces correct state via ledger recalculation.

---

## Phase 6: Polish

### Task 18: KSC Spending Actions

**Overview:** Implement KSC-time spending actions — tech node unlock, facility upgrade, kerbal hire. These happen outside flight recordings.

**Modifies:** Need investigation — how does Parsek currently intercept KSC actions? May need new `GameEvents` subscriptions or Harmony patches.

Functionality:
- Detect tech node unlock: `GameEvents.OnTechnologyResearched`
- Detect facility upgrade: facility event subscription
- Detect kerbal hire: `GameEvents.onKerbalAdded` (verify API)
- Write spending actions directly to ledger (no sidecar)
- Check reservation before allowing (block if insufficient)

**Depends on:** Tasks 2, 4-8.
**Done when:** KSC spendings are recorded in ledger and reservation blocks overspending.

---

### Task 19: UI Integration

**Overview:** Display available balances, resource breakdowns, action history in Parsek's UI.

**Modifies:** `ParsekUI.cs`

Functionality:
- Replace current resource display with ledger-derived available balances
- Breakdown panel: earned, reserved, available per resource
- Action history: scrollable list of game actions with details

**Depends on:** All modules.
**Done when:** UI shows correct ledger-derived values.

---

### Task 20: Old System Deprecation and Migration

**Overview:** Replace the existing `ResourceApplicator`/`ActionReplay`/`MilestoneStore`/`ResourceBudget` pipeline with ledger-based equivalents. This is the critical transition task.

**Modifies:** `ParsekScenario.cs`, `ParsekFlight.cs`, `ResourceApplicator.cs`, `ActionReplay.cs`, `ResourceBudget.cs`, `MilestoneStore.cs`, `GameStateStore.cs`

**Existing call sites to replace (in ParsekScenario/ParsekFlight):**
- `ResourceApplicator.TickStandalone/TickTrees` → ledger recalculation + patch
- `ResourceApplicator.DeductBudget` → ledger recalculation + patch
- `ResourceApplicator.CorrectToBaseline` → ledger recalculation + patch
- `ActionReplay.ReplayCommittedActions` → ledger recalculation + patch (covers tech, parts, facilities, crew)
- `MilestoneStore.CreateMilestone/FlushPendingEvents` → ledger commit path
- `ResourceBudget.ComputeStandaloneDelta` → ledger-derived available balances
- `GameStateStore.CommitScienceSubjects` → ledger science module

**Harmony patch transition:**
- `ScienceSubjectPatch`: may become redundant (ledger patches per-subject totals directly). Keep as fallback during transition, remove when verified.
- `TechResearchPatch`/`FacilityUpgradePatch`: blocking logic should be replaced by ledger reservation checks. These patches currently check `MilestoneStore` — redirect to ledger.

**Migration for existing saves:**
- First-load detection: no ledger file exists → seed ledger from current game state + existing `MilestoneStore` events
- Feature flag: `ParsekSettings.useLedgerSystem` for gradual rollout
- Backward compat: saves without ledger load using old system (flag=false)

**Tests:** Migration scenarios — old save loads, ledger seeded from game state, old ResourceApplicator calls replaced. ~8 tests.

**Depends on:** Tasks 15a/b, 17 (new system must be working before migration).
**Done when:** Old saves load and function correctly. Old system call sites replaced. Feature flag controls rollout.

---

### Task 21: Logging Audit and Test Coverage

**Overview:** Ensure every decision point in the new system has diagnostic logging. Expand test coverage to all verified scenarios from the design doc.

**Tests:** Log assertion tests for all module decision points, edge case tests for all design doc edge cases.

**Depends on:** All tasks.
**Done when:** KSP.log can reconstruct full recalculation walk. All design doc verified scenarios have corresponding tests.

---

### Task 22: Full Career End-to-End Test

**Overview:** Implement the "Full Career Mun Landing Timeline" from design doc section 12 as an end-to-end integration test. This exercises all modules in combination.

**New test file:** `Source/Parsek.Tests/FullCareerTimelineTests.cs`

Functionality:
- Build a complete Mun landing timeline programmatically (all 23 game actions from section 12)
- Run full recalculation walk
- Verify science balances, fund balances, milestone effective flags, contract state, kerbal reservations
- Test retroactive commit: add a second recording before the first, verify recalculation handles priority

**Depends on:** Tasks 4-12 (all modules).
**Done when:** Full career scenario produces correct state across all modules.

---

## Phase 7: Earning-Side Capture and Critical Gaps

These tasks address the critical functional gaps found during gameplay simulation (design doc section 13.1). Without these, Career mode cannot function correctly.

### Task 23: Vessel Build Cost and Recovery Funds Capture (D17, D18)

**Overview:** Inject vessel build cost and recovery funds into the ledger at commit time. These are the two main fund flows not yet captured.

**Modifies:** `LedgerOrchestrator.cs`, possibly `FlightRecorder.cs` or `ParsekFlight.cs`

Functionality:
- **Vessel build cost:** At commit time, compute vessel cost from `Recording.PreLaunchFunds` delta or from the vessel snapshot part costs. Create `FundsSpending` with `source=VesselBuild` and `recordingId` at the recording's launch UT. Add to ledger.
- **Vessel recovery funds:** Subscribe to `GameEvents.OnVesselRecoveryProcessing` in `GameStateRecorder`. Capture the recovery value. At commit time, convert to `FundsEarning` with `source=Recovery` and `recordingId`. The recovery UT is the recording's end UT.
- Alternative approach: compute both from `Recording.PreLaunchFunds/Science/Rep` and the final trajectory point's resource values — the delta IS the net funds change including build cost and recovery.

**Tests:** Commit a recording → verify FundsSpending(VesselBuild) and FundsEarning(Recovery) appear in ledger.

**Depends on:** Tasks 14, 16 (commit path wired).
**Done when:** Career vessel launch deducts funds, recovery credits funds in the ledger walk.

---

### Task 24: Milestone Achievement Capture (D5 from design doc section 7.6)

**Overview:** Subscribe to `GameEvents.OnProgressComplete` in `GameStateRecorder`, capture milestone data, convert to `MilestoneAchievement` actions at commit time.

**Modifies:** `GameStateRecorder.cs`, `GameStateEventConverter.cs`

Functionality:
- Subscribe to `GameEvents.OnProgressComplete` — capture `milestoneId`, `fundsAwarded`, `repAwarded`
- Store as a new `GameStateEventType` or as a `PendingMilestone` list (similar to `PendingScienceSubjects`)
- Convert to `MilestoneAchievement` actions in `GameStateEventConverter`

**Tests:** Achieve a milestone → verify MilestoneAchievement in ledger, effective=true, funds/rep flow to second-tier.

**Depends on:** Task 13 (converter exists).
**Done when:** Milestones enter the ledger and credit funds/rep correctly.

---

### Task 25: Science and Reputation Initial Seeding (D19 — CRITICAL)

**Overview:** Prevent science and reputation from being wiped to 0 on mid-career Parsek install. Add `ScienceInitial` and `ReputationInitial` action types.

**Modifies:** `GameAction.cs`, `Ledger.cs`, `LedgerOrchestrator.cs`, `ScienceModule.cs`, `ReputationModule.cs`

Functionality:
- Add `GameActionType.ScienceInitial` and `GameActionType.ReputationInitial` to the enum
- Add `Ledger.SeedInitialScience(float)` and `Ledger.SeedInitialReputation(float)` — same pattern as `SeedInitialFunds`
- In `LedgerOrchestrator.RecalculateAndPatch`, seed science from `ResearchAndDevelopment.Instance.Science` and reputation from `Reputation.Instance.reputation` on first run (same guard as funds)
- `ScienceModule` processes `ScienceInitial` to set baseline science balance
- `ReputationModule` processes `ReputationInitial` to set baseline rep

**Tests:** Empty ledger with existing science/rep → seed actions created → recalculation preserves values.

**Depends on:** Tasks 4, 9 (modules exist).
**Done when:** Mid-career install preserves existing science and reputation.

---

### Task 26: Contract Science Rewards in ScienceModule (D20)

**Overview:** `ScienceModule` doesn't process `ContractComplete` actions. Contract science rewards are lost.

**Modifies:** `ScienceModule.cs`

Functionality:
- Add `GameActionType.ContractComplete` handling in `ScienceModule.ProcessAction`
- When `action.Effective == true` and `action.TransformedScienceReward > 0`: add to `runningScience` and `totalEffectiveEarnings`
- This is a direct science pool addition (not subject-capped — contract science is a flat reward)

**Tests:** ContractComplete with scienceReward=10 → science balance increases by 10.

**Depends on:** Task 4 (ScienceModule exists).
**Done when:** Contract science rewards flow into the science balance.

---

### Task 27: Facility Destroyed State Patching (D21)

**Overview:** `KspStatePatcher.PatchFacilities` only patches levels, not destroyed/repaired state.

**Modifies:** `KspStatePatcher.cs`

Functionality:
- Read `FacilitiesModule.GetAllFacilities()` for each facility's `Destroyed` flag
- If `Destroyed == true`, set the building visual to destroyed state via `DestructibleBuilding` API
- If `Destroyed == false`, ensure building is intact
- Requires finding `DestructibleBuilding` objects by facility ID

**Tests:** Manual in-game test — crash into KSC, rewind to before crash, verify buildings show as intact.

**Depends on:** Task 15 (patcher exists), Task 11 (FacilitiesModule tracks state).
**Done when:** Building destroyed/intact visual state matches the ledger's derived state.

---

### Task 28: Per-Subject Science Patching

**Overview:** `KspStatePatcher.PatchScience` only patches the total balance, not per-subject collected totals. Science Archive progress bars and KSP's diminishing returns formula are stale after rewind.

**Modifies:** `KspStatePatcher.cs`, `ScienceModule.cs`

Functionality:
- `ScienceModule.GetAllSubjects()` already returns per-subject credited totals
- For each subject in the module's state, find the corresponding `ScienceSubject` via `ResearchAndDevelopment.GetSubjectByID(subjectId)`
- Set `subject.science = creditedTotal` and `subject.scientificValue` accordingly
- This replaces or supplements the existing `ScienceSubjectPatch` Harmony patch

**Tests:** Two recordings collecting same subject → rewind → verify Science Archive shows correct per-subject progress.

**Depends on:** Task 15a (patcher exists), Task 4 (ScienceModule tracks subjects).
**Done when:** Science Archive reflects the recalculated timeline.

---

### Task 29: KerbalsModule Integration into RecalculationEngine

**Overview:** Make KerbalsModule implement `IResourceModule` and participate in the unified walk, processing `KerbalAssignment`/`KerbalRescue`/`KerbalStandIn` actions from the ledger.

**Modifies:** `KerbalsModule.cs`, `LedgerOrchestrator.cs`, `GameStateEventConverter.cs`

Functionality:
- Generate `KerbalAssignment` actions at commit time from vessel snapshot crew data
- Register KerbalsModule as first-tier in RecalculationEngine
- Process kerbal actions in the walk (reservation, chains, retired pool)
- Keep the existing snapshot-based `RecalculateAndApply()` as a fallback/bridge during migration
- Eventually remove the separate `RecalculateAndApply()` calls from commit/rewind paths

**This is the largest remaining architectural task.** The existing KerbalsModule works well for sandbox; this task unifies it with the ledger for full Career mode support.

**Depends on:** All Phase 5 tasks complete.
**Done when:** Kerbal reservation and chains are driven by ledger actions, not recording snapshots.

---

### Task 30: Old System Code Cleanup

**Overview:** Remove all `// DISABLED: replaced by LedgerOrchestrator` commented-out code. Delete dead code paths in `ResourceApplicator`, `ActionReplay`, `ResourceBudget`. Keep `MilestoneStore` and `GameStateStore` (still used by the converter bridge).

**Depends on:** All other tasks complete and verified in-game.
**Done when:** No commented-out old system code remains. Build clean.

---

## Phase 8: Non-Critical Improvements

Lower-priority items from deferred list. None of these block Career mode functionality, but they improve correctness, mod compatibility, and edge case handling.

### Task 31: Strategy Conversion Rate Extraction (D2)

**Overview:** Extract actual conversion rates per strategy from KSP's strategy configuration files. Currently hardcoded to 1.0.

**Modifies:** `StrategiesModule.cs`

Functionality:
- Read strategy definitions from KSP's `GameDatabase` or decompile `Strategy` subclasses
- Map each `strategyId` to its actual conversion rate
- Replace `const float conversionRate = 1.0f` with a lookup

**Depends on:** Task 12 (StrategiesModule exists).
**Done when:** Strategy transforms use KSP's actual rates. Verified against stock strategies.

---

### Task 32: Contract Deadline Failure Generation (D from design doc 8.6)

**Overview:** During the recalculation walk, generate derived `ContractFail` actions for contracts whose deadline UT passes without resolution.

**Modifies:** `ContractsModule.cs`

Functionality:
- During the walk, track accepted contracts and their `DeadlineUT`
- If the walk reaches a UT past the deadline and the contract is still in `activeContracts`, generate a derived `ContractFail` event
- Apply penalties, free the slot

**Depends on:** Task 7 (ContractsModule exists).
**Done when:** Contracts with deadlines auto-fail at the correct UT during the walk.

---

### Task 33: Contract and Milestone KSP State Patching (D3, design doc 3.1) — DONE (milestones)

**Overview:** Patch `ContractSystem.Instance` and `ProgressTracking.Instance` after recalculation.

**Modifies:** `KspStatePatcher.cs`, `GameStateRecorder.cs`, `MilestonesModule.cs`

Functionality:
- **Contracts:** Reconstruct contract objects from stored ConfigNode snapshots, set correct state (Active/Completed/Failed/Cancelled). Done — see `KspStatePatcher.PatchContracts`.
- **Milestones:** Set `ProgressTracking` achieved flags from `MilestonesModule.IsMilestoneCredited()`. Done — `PatchMilestones` iterates the `ProgressNode` tree recursively, matching path-qualified IDs ("Mun/Landing") and setting/clearing `reached`/`complete` flags via reflection. Milestone ID capture in `GameStateRecorder.OnProgressComplete` now uses `QualifyMilestoneId` to path-qualify body-specific milestones via reflection on the private `CelestialBody body` field. Old recordings with bare IDs have a fallback match against `node.Id` but may be ambiguous across bodies (see D22).
- Risk: Medium-High for contracts (Contract Configurator compatibility), Low for milestones.

**Depends on:** Task 15b, Spike B findings.
**Done when:** Tracking station and Mission Control reflect the recalculated timeline.

**Resolution (milestones):** `PatchMilestones` replaces the scaffold with full implementation. Uses `ProgressNode` private field reflection (`reached`, `complete`) and protected property reflection (`IsCompleteManned`, `IsCompleteUnmanned`). Tree iteration uses `ProgressTree.Count` + index access + `node.Subtree` recursion. `QualifyMilestoneId` added as internal static method on `GameStateRecorder` for testability. `GetCreditedMilestoneIds` added to `MilestonesModule` to expose credited set for patching. 12 tests in `MilestonePatchingTests.cs`.

---

### Task 34: Rescue Contract Mechanics (D6)

**Overview:** Associate a rescue recording with a stranded kerbal. Close the stranded kerbal's open-ended reservation when the rescue recording is committed.

**Modifies:** `KerbalsModule.cs`, possibly `FlightRecorder.cs`

Functionality:
- Detect when a recording's vessel docks with or picks up a stranded kerbal
- Create `KerbalRescue` action linking the rescue recording to the stranded kerbal
- On recalculation, update the kerbal's reservation `endUT` from PositiveInfinity to the rescue recovery UT

**Depends on:** Task 29 (kerbals in engine).
**Done when:** Stranded kerbals become available after a rescue mission commits.

---

### Task 35: Warp Visual Updates for Facilities (D from design doc 3.3, 10.4) — DONE

**Overview:** Update facility visual state (destroyed/repaired/upgraded) during time warp.

**Resolution:** Warp start patches facility visuals to current state; warp exit runs full RecalculateAndPatch. Per-frame updates during warp deferred — facility events during Parsek warp are rare (only via rewind/commit), KSP itself doesn't update facilities mid-warp, and the final state is always correct on warp exit.

---

### Task 36: KSP MIA Respawn Handling (D7) — DONE

**Overview:** Handle the case where KSP respawns an MIA kerbal while Parsek has a permanent reservation.

**Modifies:** `KerbalsModule.cs`

Functionality:
- Detect when KSP sets a permanently-reserved kerbal back to Available (respawn)
- Decision: either override KSP's respawn (set back to Assigned) or accept it (close the reservation)
- Current behavior: override on next `RecalculateAndApply()`. Document this explicitly.

**Depends on:** Task 10a (kerbals reservation).
**Done when:** MIA respawn behavior is explicitly handled and tested.

**Resolution:** Already handled by `ApplyToRoster()` Step 3 — every reserved kerbal's `rosterStatus` is unconditionally set to `Assigned` on each `RecalculateAndApply` call. If KSP respawns a Dead kerbal to Available between calls, the next recalculation resets them. Tested by 5 `MiaRespawnOverride_*` tests in `KerbalReservationTests.cs`.

---

### Task 37: Retired Kerbal Cleanup UI (D10)

**Overview:** Allow players to acknowledge and remove retired kerbals from the roster, preventing pool growth over long careers.

**Modifies:** `ParsekUI.cs`, `KerbalsModule.cs`

Functionality:
- Add a "Retired Kerbals" section in the Game Actions window
- Show retired kerbal names with a "Dismiss" button
- On dismiss, remove from roster and retired tracking
- Guard: only allow dismissal of kerbals with no active recording references

**Depends on:** Task 19 (UI integration).
**Done when:** Players can manage retired kerbal pool size.

---

### Task 38: Mod Compatibility Investigation (D12, D13)

**Overview:** Test with CustomBarnKit (facility tiers) and Strategia (strategy replacement). Document compatibility status.

Functionality:
- Install CustomBarnKit, verify facility level tracking works with non-standard tiers
- Install Strategia, verify strategy module gracefully handles modded strategies
- Document findings in `docs/mods-references/`
- Add guards or fallbacks where needed

**Depends on:** All core tasks complete.
**Done when:** Compatibility status documented. Critical incompatibilities fixed or documented as known limitations.

---

## Summary

| Phase | Tasks | Description | Status |
|-------|-------|-------------|--------|
| 0. Risk Reduction | Spikes A-D | Reputation curve, contract patching, kerbal roster, KSC events | **Done** |
| Kerbals A | 10a/b + 3 tasks | End states, reservation chains, ApplyToRoster, dismissal | **Done** |
| 1. Foundation | 1-3 | Data types, ledger I/O (with seeding), recalculation engine | **Done** |
| 2. Simple Modules | 4-7 | Science (earnings + spending), milestones, contracts (first-tier) | **Done** |
| 3. Second-Tier | 8-9 | Funds (depends on milestones, contracts, kerbals), reputation | **Done** |
| 4. Complex Modules | 11-12 | Facilities, strategies | **Done** |
| 5. Integration | 13-17 | Converter, patcher, orchestrator, commit/rewind/warp wiring | **Done** |
| 6. Polish | 18-22 | KSC spendings, UI, old system deprecation, logging, end-to-end test | **Partial** (UI done) |
| 7. Critical Gaps | 23-28 | Vessel cost/recovery, milestones, science/rep seeding, contract sci, facility/science patching | Pending |
| 8. Non-Critical | 31-38 | Strategy rates, deadline gen, contract/milestone patching, rescue, warp visuals, MIA, retired UI, mod compat | **Done** (T38 mod testing deferred to T43 — needs KSP runtime) |
| 9. Architecture | 29-30 | KerbalsModule into engine, old code cleanup | Deferred (T42 — low priority, bridge pattern works) |

**Parallelization opportunities:**
- Tasks 4, 6, 7 are independent first-tier modules — can run in parallel
- Task 10a can start after Task 3 (no funds dependency)
- Tasks 8, 9 can run in parallel AFTER 10b completes (funds needs kerbal hire costs)
- Tasks 13, 14 are sequential

**Parallelization caveats (NOT parallelizable despite sharing a phase):**
- Task 11 (Facilities) depends on Task 8 (Funds) for upgrade/repair cost flow
- Task 12 (Strategies) depends on Tasks 7, 8, 9 for contract reward transforms
- Tasks 11, 12 must wait for their second-tier dependencies, not just Task 3

**Key dependency chain:** Spikes → 1 → 2 → 3 → {4,6,7,10a} → 10b → {8,9} → {11,12} → {15a,15b} → {16,17} → 20

**Performance note:** Full recalculation walk from UT=0 on every warp exit. With typical career playthroughs (10-50 recordings, hundreds of actions), this should be sub-millisecond. At extreme scale (100+ recordings, thousands of actions), profile before optimizing — the sort is O(n log n) and the walk is O(n) with small constant factors.

**Estimated new tests:** ~180-200 tests across all modules (including log assertion tests per module)
**Estimated new code:** ~4000-5000 lines (excluding tests)
