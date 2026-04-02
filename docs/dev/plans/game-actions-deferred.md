# Game Actions & Resource System — Deferred Items

Items identified during design that are out of scope for the initial implementation. Each has a justification and a trigger for when to revisit.

---

## Deferred from Design Doc — Open Questions

### D1. Reputation Curve Formula Extraction

**What:** Extract KSP's exact reputation gain/loss curve from decompiled `Reputation` class (the grain system and gain/loss asymmetry).
**Why deferred:** Requires decompilation investigation before implementation can begin. The Reputation module depends on this.
**Revisit when:** Starting Reputation module implementation (Task for reputation).
**Status:** Done (Spike A) — exact keyframes extracted from decompiled Assembly-CSharp.dll. See `game-actions-spike-findings.md`.

---

### D2. Strategy Conversion Rates

**What:** Extract exact conversion rates per strategy (how much target resource per unit of diverted source) from KSP's strategy configuration or decompiled code.
**Why deferred:** Requires investigation of KSP's strategy definitions. Low-priority module.
**Revisit when:** Starting Strategies module implementation.
**Status:** Open

---

### D3. Contract Parameter Preservation

**What:** Investigate how much of KSP's `Contract` internal state (completion conditions, parameters, waypoints) can be serialized and restored during patching.
**Why deferred:** Complex investigation with unknown scope. Contract patching may require storing a serialized contract snapshot at accept time.
**Revisit when:** Starting Contracts module KSP state patching.
**Status:** Open

---

### D4. Contract Generation Seeding

**What:** Determine whether KSP's procedural contract generation RNG needs to be seeded for consistent offerings after rewind, or if divergence is acceptable.
**Why deferred:** Likely acceptable to allow divergence — patching accepted/completed contracts prevents duplicates. Investigate only if players report confusion.
**Revisit when:** Player feedback on contract behavior after rewind.
**Status:** Open

---

### D5. Tourist Contracts

**What:** Tourist contracts place passenger kerbals on the vessel. These are temporary — they leave after recovery. Determine whether the ledger needs to track tourists.
**Why deferred:** Tourists are purely a contract concern, not managed kerbals. No reservation or chain needed. Low priority.
**Revisit when:** Contracts module is otherwise complete.
**Status:** Open

---

### D6. Rescue Contract Mechanics

**What:** How the ledger associates a rescue recording with a stranded kerbal. Requires the recording system to detect docking with or picking up a stranded kerbal.
**Why deferred:** Requires cross-system integration between recording system and kerbals module. Complex edge case.
**Revisit when:** Kerbals module basic reservation is working and stranded state is implemented.
**Status:** Scaffolded — requires recording system integration to detect docking with stranded kerbals. Placeholder added in GameStateEventConverter.cs (Phase 8, Task 34).

---

### D7. KSP MIA Respawn

**What:** KSP can respawn MIA kerbals after a configurable delay. Determine whether Parsek should model this or treat MIA as permanently gone.
**Why deferred:** Edge case. Most players don't rely on MIA respawn. Can let KSP handle it outside the ledger.
**Revisit when:** Kerbals module is complete. Player feedback.
**Status:** Open

---

### D8. Stand-in Naming Strategy

**What:** Whether to use KSP's procedural name generator or maintain Parsek's own. KSP's generator avoids collisions with existing roster.
**Why deferred:** Implementation detail, decide during Kerbals module implementation.
**Revisit when:** Implementing stand-in generation.
**Status:** Open

---

### D9. Sandbox Kerbal Chain Simplification

**What:** In sandbox, replacement is trivial (free, unlimited). Determine whether sandbox needs the full chain system or just the reservation check.
**Why deferred:** Sandbox is lowest-priority game mode. Reservation alone prevents duplicate kerbals.
**Revisit when:** After career kerbals module is working.
**Status:** Open

---

### D10. Retired Kerbal Cleanup

**What:** Whether there should be a mechanism for the player to permanently remove retired kerbals from the roster (the pool can grow over a long career).
**Why deferred:** UX concern. Blocking dismissal is the safe v1 default.
**Revisit when:** Player feedback on retired pool size.
**Status:** Open

---

### D11. Facility Destruction Detection API

**What:** Investigate exactly how KSP detects and reports building destruction during flight. Confirm the events and API surface.
**Why deferred:** Requires KSP API investigation.
**Revisit when:** Starting Facilities module implementation.
**Status:** Open

---

### D12. CustomBarnKit Interaction

**What:** CustomBarnKit modifies facility costs and progression tiers. Determine if the ledger needs to account for non-standard level counts or costs.
**Why deferred:** Mod compatibility. Record what KSP reports; don't assume standard values.
**Revisit when:** After basic facilities module works with stock KSP.
**Status:** Open

---

### D13. Strategy Mod Compatibility (Strategia)

**What:** Popular mod Strategia completely replaces the stock strategy system. Determine whether to support modded strategies.
**Why deferred:** v1 targets stock only. Strategy module is already lowest-priority.
**Revisit when:** Player demand for mod support.
**Status:** Open

---

### D14. Reputation Floor Behavior

**What:** Can reputation go below -1000? Does the loss curve diminish near the floor like the gain curve does near the ceiling?
**Why deferred:** Requires decompilation investigation alongside D1.
**Revisit when:** Reputation curve extraction (D1).
**Status:** Done (Spike A) — Loss curve multiplier approaches ~0.0x near rep=-1000 (symmetric with gain ceiling). No hard clamp in `AddReputation`, but `SetReputation` clamps to [-1000, 1000]. Effectively, rep cannot go significantly below -1000.

---

### D15. Science Mode Milestone Tracking

**What:** In Science mode, milestones track progression but award nothing. Determine whether the ledger needs to track them for progression gating, or if KSP's native Progress Tracking is sufficient.
**Why deferred:** Low priority — Science mode is simpler than Career.
**Revisit when:** After Career milestones are working.
**Status:** Open

---

### D16. VAB/SPH Editing Sessions

**What:** Whether the ledger needs to track vessel cost changes during editing (parts added/removed), or only the final cost at launch.
**Why deferred:** Only the final cost at launch seems necessary — the ledger doesn't track what happened in the editor.
**Revisit when:** Funds module vessel build cost implementation.
**Status:** Open — likely WONT-DO (only final launch cost matters)

---

### D17. Vessel Build Cost Capture

**What:** No converter path creates `FundsSpending` with `source=VESSEL_BUILD` for vessel launch costs.
**Why deferred:** `FundsChanged` events are aggregate deltas, not discrete actions. Needs either a `VesselLaunched` event type or commit-time injection from recording metadata.
**Revisit when:** Implementing earning-side capture (remaining Phase 6 work).
**Status:** Open

---

### D18. Vessel Recovery Funds Capture

**What:** No converter path creates `FundsEarning` with `source=RECOVERY` for vessel recovery value.
**Why deferred:** `OnVesselRecoveryProcessing` not yet subscribed for ledger purposes.
**Revisit when:** Implementing earning-side capture.
**Status:** Open

---

### D19. Science and Reputation Initial Seeding

**What:** Mid-career Parsek install wipes science and reputation to 0. Need `ScienceInitial` and `ReputationInitial` action types.
**Why deferred:** Only affects first-time install on existing saves. Funds seeding works correctly.
**Revisit when:** Before first release to players.
**Status:** Done — `ScienceInitial` (enum 21) and `ReputationInitial` (enum 22) action types added. Seeded in `LedgerOrchestrator.RecalculateAndPatch` alongside funds. Processed by `ScienceModule.ProcessScienceInitial` and `ReputationModule.ProcessReputationInitial`. Preserved during reconciliation.

---

### D20. Contract Science Rewards

**What:** `ScienceModule` doesn't process `ContractComplete` actions. Science from contract completion is lost.
**Why deferred:** Rare in stock KSP (few contracts award science). Easy fix.
**Revisit when:** Before Career mode testing.
**Status:** Done — `ScienceModule.ProcessContractScienceReward` added. Uses `TransformedScienceReward` (post-strategy), only processes when `Effective=true`.

---

### D21. Facility Destroyed State Patching

**What:** `KspStatePatcher.PatchFacilities` only patches levels, not destroyed/repaired state.
**Why deferred:** Requires `DestructibleBuilding` API investigation for programmatic destruction/repair.
**Revisit when:** Implementing warp visual updates.
**Status:** Done — `PatchDestructionState` added to KspStatePatcher. Collects DestructibleBuilding objects once, matches by facility ID, calls Demolish()/Repair() as needed.

---

## Deferred from Implementation

(Items will be added here as they surface during task implementation.)
