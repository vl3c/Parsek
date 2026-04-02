# Parsek — Game Actions & Resource System Design

**Version:** 0.3 (post-implementation update — reflects Phases 1-5 actual code)
**Status:** Core modules implemented and tested (4458 tests). Integration wired. Earning-side capture, per-subject science patching, contract deadline generation, and warp visual updates remain as future work.
**Scope:** Everything the timeline tracks beyond vessel trajectories — science, funds, reputation, milestones, contracts, kerbals, facilities, strategies.
**Out of scope:** Vessel recording system, DAG structure, ghost rendering, distance zones. See `parsek-recording-system-design.md` for those.

---

## 1. Architecture Overview

The game actions system is a **standalone module** that tracks every resource-related event on the timeline. It is completely separate from the vessel recording system. It does not know about trajectories, DAGs, ghosts, or flight recordings. The vessel recording system does not know about science, funds, or tech nodes.

### 1.1 Coupling between systems

The coupling between the two systems is exactly one field: `recordingId` on earning actions. When a recording is committed, its associated earning actions join the timeline. The recording system doesn't know about science values. The ledger doesn't know about trajectories. They share a key.

### 1.2 API between systems

**Recording system → Ledger:**

- `OnRecordingCommitted(recordingId, startUT, endUT)`

There is no deletion event. Parsek doesn't delete recordings. KSP load handles removal natively by not including the recording in the loaded save.

**Ledger → Recording system:**

- `GetAvailableResources(ut)` → current balances at a point in time
- `GetRecordingDeltas(recordingId)` → list of game actions for UI display

**Implementation (v0.3):** The actual API is `LedgerOrchestrator.OnRecordingCommitted(recordingId, startUT, endUT)` for the commit trigger. `GetAvailableResources` and `GetRecordingDeltas` are not yet implemented as named methods — module query methods (`FundsModule.GetAvailableFunds()`, `ScienceModule.GetAvailableScience()`, etc.) provide this data.

### 1.3 Three kinds of game actions

**Earning actions** are associated with a recording ID. Science collected during a mission, funds from vessel recovery, reputation from milestones, kerbal assignments. Tagged with the recording that produced them. Removed from the timeline only if the recording is removed (which only happens via KSP load/hard reset — Parsek has no delete operation).

**Spending actions** are not associated with any recording. Tech node purchases, facility upgrades, kerbal hires. These happen at KSC between missions. They persist on the timeline. Removed only by loading a save from before their UT.

**System-generated actions** are created by Parsek itself in response to earning or spending actions. Kerbal stand-in generation is the primary example — when a kerbal is temporarily reserved by a committed recording, Parsek generates a replacement kerbal to maintain roster stability. These are not player actions and have no UT on the timeline, but they do modify KSP's game state (roster entries).

### 1.4 Timeline operations

| Operation | Effect on game actions |
|-----------|----------------------|
| COMMIT    | Add recording's earning actions to timeline, trigger recalculation |
| HIDE      | Stop ghost playback; game actions remain on timeline |
| UNHIDE    | Restore ghost playback; no change to game actions |
| REWIND    | Non-destructive jump to a recording's launch point; all actions survive, trigger recalculation |
| LOAD      | Destructive reset to save state (KSP native); Parsek rebuilds from save contents |

There is no delete button. No deficit system is needed. Load is the only destructive path.

### 1.5 The no-delete invariant

Because recordings can only be added to the timeline (never removed by the player through Parsek), the total earnable resources can only stay the same or increase over time. A retroactive commit can redistribute credit between recordings but cannot reduce the total credited amount for any capped resource. This means **existing** spending actions that were valid when created cannot be retroactively invalidated by normal play.

However, the invariant does not prevent the player from adding **new** spendings after a rewind that would exceed the available budget. Each new recording adds a vessel build cost (funds), and the player can unlock new tech nodes (science) or hire kerbals (funds) at KSC. Without further protection, these new spendings could create a deficit downstream.

The **reservation system** (sections 4.5 and 5.6) covers this gap: all committed spendings — past, present, and future — are reserved against total earnings. The player can only add new spendings if the available balance (earnings minus all reservations) is sufficient. Together, the no-delete invariant protects existing spendings, and the reservation system prevents new overspending.

### 1.6 Recalculation triggers

| # | Trigger |
|---|---------|
| 1 | Recording committed |
| 2 | Rewind (load Parsek quicksave) |
| 3 | Fast-forward crosses a game action |
| 4 | Save loaded (KSP load — Parsek rebuilds from save contents) |

### 1.7 Sort order for the unified walk

All game actions across all resource modules are collected and sorted for the recalculation walk. The sort key is:

| Priority | Field | Notes |
|----------|-------|-------|
| Primary | UT ascending | When the action occurred on the timeline |
| Secondary | Type: earnings before spendings | At the same UT, credits before debits |
| Tertiary | Sequence index | Order within spendings at the same UT (e.g. multiple tech node unlocks at frozen KSC time) |

The sequence index is only meaningful for spending actions. Earnings at the same UT are additive and order-independent.

### 1.8 Recalculation model

On any timeline change (commit, rewind, fast-forward event, KSP load), every resource module recalculates its derived state from scratch by walking all actions from UT=0 forward. No module retains cached state between recalculations — the ledger file is the sole input, the derived state is the sole output.

The general pattern is:

```
Recalculate():
  1. Reset all derived state to zero/default.
  2. Collect all game actions from the ledger file.
  3. Sort by (UT, type, sequence).
  4. Walk forward from UT=0:
     For each action:
       Dispatch to the appropriate module.
       Module updates its derived state (running balances, caps, reservations, visual state).
  5. Derived state at the end of the walk = current state.
```

Each module owns its portion of the walk:

- **Science**: tracks per-subject credited totals and running science balance. Applies subject caps.
- **Funds**: tracks running funds balance. Validates affordability.
- **Reputation**: tracks running reputation balance.
- **Milestones**: tracks once-ever flags. Chronologically first recording gets credit.
- **Kerbals**: computes reservations, chain occupancy, XP totals.
- **Facilities**: derives building level and visual state (upgraded/destroyed/repaired).

**Dependency ordering.** Some modules produce values that other modules consume. The recalculation respects a priority order:

| Tier | Modules | Role |
|------|---------|------|
| First (independent) | Science, Milestones, Contracts | Determine what was earned, achieved, or assigned. Compute effective values. |
| Transform | Strategies | Apply active strategy transforms to effective contract rewards before second-tier crediting. |
| Separate | Kerbals | Reservation/chains from recording snapshots (see 9.15) |
| Second (dependent) | Funds, Reputation | Aggregate effective (and transformed) earnings and spendings from first-tier modules. |
| Parallel | Facilities | Derives building state and feeds repair/upgrade costs into Funds. |

First-tier modules run their walk and produce effective values. Strategy transforms then modify contract rewards within their active windows. Once transforms are applied, the milestone and contract fund/reputation awards flow into the Funds and Reputation running balances in the second tier. This means a single recalculation pass walks all actions from UT=0 forward, but within each UT, first-tier modules resolve, then strategy transforms apply, then second-tier modules consume the results.

Modules remain isolated in logic — they don't call each other. The dependency is purely data flow: first-tier modules write effective values onto actions, second-tier modules read those values during their portion of the same walk.

Events on the timeline are locked once committed — their immutable values never change. But the **derived** values are recomputed on every recalculation. Adding a new recording to the past can change derived values on future events (e.g. a retroactive science collection reduces a later collection's effective credit). The events themselves are fixed; only the interpretation changes.

### 1.8.1 Implementation Notes (Post-Build)

**Derived field reset:** Before each walk, the engine resets all derived fields on every action to defaults (`Effective=true`, `EffectiveScience=0`, `Affordable=false`, `EffectiveRep=0`, `TransformedFundsReward=FundsReward`, etc.). This prevents stale values from a previous recalculation from leaking into the next walk. Critical for idempotency — calling Recalculate twice on the same actions must produce identical results.

**PrePass phase:** Between Reset and the walk, the engine calls `PrePass(actions)` on every module. `ScienceModule` and `FundsModule` use this to compute total committed spendings across the entire timeline (needed for the reservation formula). Other modules have no-op PrePass implementations.

**Strategy transform mechanism:** Strategy transforms modify `TransformedFundsReward`, `TransformedScienceReward`, and `TransformedRepReward` derived fields — NOT the persisted `FundsReward`/`ScienceReward`/`RepReward`. Second-tier modules read the Transformed fields. This prevents data corruption if the ledger is saved between recalculations.

**Orchestration:** `LedgerOrchestrator` (not in the original design) coordinates the full pipeline: creates module instances, registers them with the engine, converts `GameStateEvent` data to `GameAction` entries at commit time, and invokes the recalculate-then-patch sequence on all four triggers.

**Event conversion bridge:** `GameStateEventConverter` maps `GameStateEvent` entries from `GameStateStore` to `GameAction` entries for the ledger. This replaces the design doc's "extract from sidecar" flow — game actions are captured by `GameStateRecorder` into `GameStateStore` during flight, then converted to `GameAction` entries at commit time. No sidecar involvement for game actions.

### 1.9 Spending reservation pattern

Any resource that has both earnings and spendings requires a **spending reservation system** to prevent the player from adding new spendings that would create a deficit at any point on the timeline.

When the player rewinds to an earlier UT, all committed spendings on the timeline (past and future relative to the current UT) are reserved against the available earnings. The player can only add a new spending if the available balance covers it.

```
available(ut) = sum(effective earnings up to ut) - sum(ALL committed spendings on entire timeline)
```

This applies to:

- **Science**: available science = effective science earned up to current UT minus ALL committed tech node costs. Prevents unlocking tech nodes that would create a science deficit.
- **Funds**: available funds = seed + fund earnings up to current UT minus ALL committed fund spendings (vessel builds, facility upgrades, hires, etc.). Prevents building vessels or upgrading facilities beyond the budget.

**Reputation does NOT need a spending reservation.** Reputation can go negative without blocking any player action (unlike funds or science, where negative balance prevents launching or unlocking). The only reputation spending that requires a minimum balance is strategy activation — and strategies are already gated by UT=0 reservation (section 11.3), which blocks new strategy activations entirely while existing ones are on the timeline. Contract decline and failure penalties always apply regardless of current rep level.

The reservation pattern is analogous to the kerbal reservation system — future committed events lock resources retroactively.

**The no-delete invariant protects existing spendings.** Adding a recording can only add new earnings (and for funds, a vessel cost). It cannot remove existing earnings. For science, subject caps mean the total credited science is stable — redistribution happens, but the total doesn't decrease. So existing spendings that were valid when committed remain valid.

**The reservation system protects against new spendings.** Even though existing spendings stay valid, the player could attempt to add new spendings (tech nodes, facility upgrades) at a UT where the running balance can't support them alongside existing future spendings. The reservation blocks this.

Together, the two mechanisms guarantee that the walk never shows a negative balance for science or funds. Reputation can go negative by design (penalties are unconditional).

### 1.10 Three layers of paradox prevention

Parsek prevents time-travel paradoxes through three complementary design layers:

**Layer 1: No-delete invariant.** Recordings can only be added to the timeline, never removed. This guarantees that total earnable resources never decrease. Existing spendings that were valid when committed cannot become invalid — there is no scenario in normal play where an earlier-valid spending becomes unaffordable due to a new recording being added.

**Layer 2: Spending reservation.** All committed spendings — past, present, and future — are reserved against available earnings. The player can only add new spendings (vessel builds, tech nodes, facility upgrades, kerbal hires) if the available balance covers them. This prevents the player from creating new deficits after rewinding to an earlier UT. Applies to science and funds.

**Layer 3: UT=0 reservation.** Identity and slot resources are locked from the start of time when committed anywhere on the timeline. Kerbals used in a recording are reserved from UT=0 as a continuous block — no gaps, no reuse until all recordings resolve. Contracts and strategies consume their slots from UT=0 until resolved or deactivated. This prevents duplicate kerbals, slot overflow, and resource diversion conflicts that could cascade into downstream paradoxes.

**Core philosophy: conservative by design.** Every restriction exists to prevent a specific paradox. The tradeoff is reduced gameplay flexibility — kerbals can't be reused freely across rewinds, strategies can't be changed while existing ones are committed, available funds may show zero at early UTs because the budget is fully committed to future events. But the player never sees a broken timeline, never encounters an unresolvable deficit, and never needs to manually fix inconsistencies. If a situation becomes truly stuck, KSP load (the hard reset) is always available as the escape hatch.

### 1.11 Resource modules

Each resource type is an independent module with its own earning/spending rules. All modules feed into the same unified timeline walk, which dispatches each action to its module. The modules are, ordered by game mode and increasing complexity:

| Game mode | Module | Earnings | Spendings |
|-----------|--------|----------|-----------|
| Sandbox | Kerbals | Assignment, XP gain, death/MIA | — (free creation) |
| Science | Science | Experiments (transmit/recover) | Tech node unlocks |
| Science | Kerbals | Assignment, XP gain, death/MIA | — (no funds) |
| Career | Funds | Contracts, recovery, milestones | Vessel builds, facility upgrades |
| Career | Reputation | Contracts, milestones | Contract failures, strategies |
| Career | Milestones | World Firsts triggers (award funds + rep) | — |
| Career | Contracts | Accept/complete/fail/cancel (award/penalize funds + rep) | — |
| Career | Kerbals | Hiring, assignment, XP gain, death/MIA | Hiring cost (funds) |
| Career | Facilities | — | Upgrade cost (funds), gates capabilities |
| Career | Strategies | Ongoing resource conversion rates | — |

### 1.12 Module documentation template

Each resource module chapter should follow this structure when fully designed:

1. **Status** and **Game mode applicability** — which modes the module is active in and how behavior varies.
2. **KSP mechanics** — how the vanilla game handles this resource (the ground truth Parsek must model).
3. **Earning action schema** — fields stored in the ledger for incoming resources.
4. **Spending action schema** — fields stored for outgoing resources (if applicable).
5. **Recalculation logic** — how the unified walk processes this module's actions, including caps, constraints, and priority rules.
6. **Verified scenarios** — worked examples showing correct behavior under normal play and edge cases (retroactive commits, rewinds, paradox prevention).
7. **Edge cases** — known tricky situations and how the system handles them.
8. **Open questions** — unresolved design decisions for future work.

---

## 2. Persistence Architecture

### 2.1 File layout

The game actions system persists its data in a **dedicated ledger file** on disk, separate from both the KSP save file and the recording sidecar files. The ScenarioModule in the .sfs save file holds a reference to this ledger file (same pattern as recording sidecar files).

```
saves/
  savegame/
    persistent.sfs              ← KSP save file (ScenarioModule references the ledger file)
    Parsek/
      GameState/
        ledger.pgld             ← single file: all earnings + spendings, all resource types
      recordings/
        rec_001.dat             ← sidecar: trajectory + raw science events (rich, verbose)
        rec_002.dat
        ...
```

The ledger file contains all game actions across all resource modules — science earnings, science spendings, fund transactions, milestones, contracts, kerbal events, facility upgrades. One file, one source of truth for recalculation.

### 2.2 Data flow

**During flight:** KSP callbacks (e.g. `OnScienceReceived`) fire as events happen. Event data is captured by `GameStateRecorder` into `GameStateStore` (in-memory). The ledger file is not touched during flight.

**On commit:** `GameStateEventConverter` transforms `GameStateStore` events into `GameAction` entries and appends them to the ledger. The `GameStateStore` events are the source; no sidecar files are involved for game actions.

**On recalculation:** The ledger file is the sole data source. All entries are loaded, sorted, and walked. No sidecar files are opened.

**Spending actions (KSC):** Tech node unlocks, facility upgrades, kerbal hires — these happen outside any flight recording. They are written directly to the ledger file with no sidecar involvement.

**Implementation divergence:** The actual data flow uses `GameStateRecorder` → `GameStateStore` (in-memory) → `GameStateEventConverter` → Ledger (at commit time). Raw events are NOT written to recording sidecar files — they are captured in memory during flight and converted to `GameAction` entries when the recording is committed. The sidecar retains trajectory/part event data only.

### 2.3 KSP load reconciliation

When KSP loads a save, the ScenarioModule provides the ledger file reference and the list of committed recording IDs. Parsek reconciles the ledger file against the loaded state:

1. **Earning actions:** Any entry whose `recordingId` is not in the loaded save's recording list is pruned from the ledger file.
2. **Spending actions:** Any entry whose UT is after the loaded save's current UT is pruned.
3. **Recalculation** runs on the pruned dataset.

After reconciliation, the ledger file reflects the loaded save's state. The pruned entries are lost (the sidecar files still have the raw earning data as a passive backup, but Parsek never reads from them to restore ledger state).

### 2.4 Why not ScenarioModule or sidecar-only

The ledger data was considered for storage inside the ScenarioModule (embedded in the .sfs save file) or solely within recording sidecar files. Both were rejected:

**ScenarioModule (inside .sfs):** Would make KSP load semantics automatic (load = correct state for free), but embeds structured data inside KSP's own save format, harder to inspect and manage externally.

**Sidecar-only:** Would give a single source of truth with zero duplication, but makes recalculation depend on the file system (reading N sidecar files per recalc), creates coupling between the ledger module and recording internals, and makes the system fragile to sidecar corruption or deletion.

**Dedicated ledger file:** Self-contained for recalculation (no file system traversal), independent from recording internals (data is extracted at commit time and never read from sidecars again), inspectable on disk, and follows the same reference pattern already established for sidecar files.

### 2.5 Initial state seeding

Some resources start at non-zero values when a career save is created. The ledger must be seeded with these initial values, extracted from the save file — not assumed from defaults, as the player may use custom difficulty settings or mods that change starting values.

Currently, only **funds** requires seeding (career starting funds vary by difficulty). Science and reputation start at 0 in all difficulty presets. If future modules require non-zero initial values, the same extraction-from-save pattern applies.

The seed is written to the ledger file once when Parsek first initializes on a career save. It is immutable.

---

## 3. KSP State Patching & Warp Model

After every recalculation, the ledger's derived state may differ from what KSP's internal systems believe is true. Parsek must patch KSP's state to match. This section describes what gets patched, when, and how the warp cycle works.

### 3.1 What gets patched

Each module has a corresponding KSP internal state that must agree with the ledger's derived values at the current UT:

| Module | KSP internal state | What Parsek patches |
|--------|--------------------|-------------------|
| Science | `ResearchAndDevelopment.Instance` | Per-subject collected totals, total science balance, tech tree unlock state |
| Funds | `Funding.Instance` | Fund balance |
| Reputation | `Reputation.Instance` | Reputation balance |
| Milestones | `ProgressTracking.Instance` | Set of achieved milestones |
| Kerbals | `HighLogic.CurrentGame.CrewRoster` | Roster entries (active, reserved, retired, stand-ins), XP per kerbal, alive/dead/MIA status |
| Facilities | `ScenarioUpgradeableFacilities` | Building levels, destroyed/intact state |

The Science Archive in the R&D building reads from `ResearchAndDevelopment.Instance`. Patching the per-subject collected totals ensures the Science Archive progress bars reflect the recalculated timeline, not KSP's stale state.

**Implementation status (v0.3):** Science (balance only — per-subject totals not yet patched), Funds, Reputation, and Facilities are patched. Milestones (`ProgressTracking`) and Contracts (`ContractSystem`) are not yet patched. Kerbals are patched separately via `KerbalsModule.ApplyToRoster` (not through `KspStatePatcher`).

**Critical: use `SetReputation`, NOT `AddReputation`.** KSP's `AddReputation` applies a nonlinear gain/loss curve. If the recalculation engine already computed the final reputation value through the curve simulation, calling `AddReputation` with a correction delta would apply the curve TWICE, causing significant distortion. `SetReputation` directly assigns the value. This was discovered via decompilation (see spike findings).

### 3.2 Two-phase pattern

Every timeline change follows the same two phases:

```
Phase 1: RECALCULATE
  Walk ledger from UT=0 to current UT.
  Compute all derived values. Pure computation — no KSP mutation.
  Output: correct state per module at current UT.

Phase 2: PATCH
  For each module:
    Read derived state at current UT.
    Write into KSP's corresponding internal state.
  KSP now agrees with the ledger.
```

The recalculation is pure — it reads the ledger and produces derived state. The patching is impure — it mutates KSP's runtime objects. Keeping these separate means the recalculation logic can be tested without KSP running, and the patching is a thin translation layer.

### 3.3 Warp cycle

**Status: NOT YET IMPLEMENTED.** The warp model below is designed but the implementation (facility visual updates during warp, event queue) is future work. The warp exit recalculation trigger IS wired.

During time warp (fast-forward), the player is not in control. KSP state mutation is deferred until the player exits warp. The warp cycle follows the same collect-during-warp, process-on-exit pattern used for vessel spawning.

```
ENTER WARP:
  KSP state is already correct (patched after last recalculation).
  Start visual playback. No recalculation needed.

DURING WARP:
  Visual-only updates (no KSP state mutation):
    - Ghost vessels replay from sidecar trajectory data.
    - Facility visuals update at the correct UT (building destroyed,
      repaired, upgraded — rendered in real time like ghosts).
  Event queue collects:
    - Game action UTs crossed during warp.
    - Vessel spawn/despawn events (existing recording system).
  No recalculation. No KSP state patching.

EXIT WARP:
  Full recalculation from UT=0 to current UT.
  Patch all KSP state (R&D, funds, rep, roster, facilities, milestones).
  Process vessel spawn queue (spawn/despawn real vessels).
  Player takes control with correct state.
```

Key properties of this model:

**One recalculation per warp session.** The full walk from UT=0 runs once on exit, not per event crossing. This is a significant performance benefit — warping through 50 science events runs one recalculation, not 50.

**Visuals are real-time, state is deferred.** Ghost vessels and building visual changes render at the correct UT during warp, exactly as the player would expect. But KSP's internal state (fund balance, science totals, roster) only updates on exit. The player never interacts with KSP state during warp, so the deferral is invisible.

**Two parallel queues.** The vessel spawn queue and game action queue follow the same pattern — collect during warp, process on exit. The game action queue primarily serves as a trigger for recalculation and a source for the player feedback summary on exit ("During warp: 3 recordings completed, 45 science earned, First Mun Landing achieved, Jeb recovered").

### 3.4 Patching after rewind

Rewind is a special case. KSP loads a quicksave, which restores KSP state to the moment the quicksave was taken. But the timeline may have recordings committed after that quicksave was created. The quicksave doesn't know about those recordings.

```
REWIND:
  1. KSP loads quicksave (KSP state = quicksave state).
  2. Parsek recalculates full timeline from UT=0 to rewind UT.
  3. Parsek patches KSP state to match ledger's derived state.
  Player takes control with state reflecting all committed recordings.
```

The quicksave is the starting point, but Parsek overrides it with the ledger's truth. This is why per-subject science totals, fund balances, and kerbal roster may differ from what the quicksave contained — the ledger accounts for recordings that were committed after the quicksave was taken.

### 3.5 Patching after KSP load

KSP load is authoritative — the loaded save defines truth. Parsek reconciles the ledger (prunes entries not in the save), then recalculates and patches:

```
KSP LOAD:
  1. KSP loads save (KSP state = save state).
  2. Parsek reconciles ledger (prune orphaned earnings and future spendings).
  3. Parsek recalculates from UT=0 to loaded UT.
  4. Parsek patches KSP state.
```

In most cases after a KSP load, the patched state will match what's already in the save. The recalculation is a verification step — it ensures consistency even if the save was edited or corrupted.

---

## 4. Science Module

**Status:** Designed — earning/spending schemas, recalculation algorithm, reservation system, and verified scenarios complete.

Science is the simplest resource module and the first to be fully designed.

**Game mode applicability:** Not applicable in sandbox (all tech unlocked, no science tracking). Active in Science mode and Career mode with identical behavior in both.

### 4.1 KSP's science pipeline

1. Player uses a science instrument at a specific body + situation + biome.
2. KSP generates a `ScienceData` with a subject ID (e.g. `crewReport@MunSrfLandedMidlands`).
3. Player transmits (partial value, scaled by `transmitScalar`) or recovers the vessel (full value).
4. KSP adds science via `ResearchAndDevelopment.Instance.SubmitScienceData()`.
5. Value diminishes on repeat — the diminishing returns formula is roughly `nextValue = baseValue × subjectMultiplier × (1 − subjectScience / scienceCap)`.

### 4.2 Single subject cap

There is one `scienceCap` per subject. Both transmit and recover draw from the same pool. Transmitting depletes the subject by the awarded amount (post-scalar). Recovery depletes by the full awarded amount. There are no separate transmit and recovery budgets — all methods eat from the same cap.

The `scienceAwarded` value already has the transmit scalar baked in. It is the post-method number. No raw pre-transmit value needs to be tracked.

### 4.3 Science earning action schema

```
ScienceEarning
  ut:                double — when it happened
  recordingId:       string — which recording earned this
  subjectId:         string — full KSP subject string (e.g. "crewReport@MunSrfLandedMidlands")
  experimentId:      string — the experiment type (e.g. "crewReport")
  body:              string — e.g. "Mun"
  situation:         string — e.g. "SrfLanded"
  biome:             string — e.g. "Midlands"
  scienceAwarded:    float — what KSP actually credited (IMMUTABLE, never changes)
  method:            enum — TRANSMITTED | RECOVERED
  transmitScalar:    float — the experiment's transmission efficiency (0.0 to 1.0)
  subjectMaxValue:   float — total science this subject can yield (scienceCap)
```

`scienceAwarded` is immutable. Written once at recording time. What KSP reported. Never changes. Captured by `GameStateRecorder` during flight, converted to ledger entries on commit.

`effectiveScience` is derived. Always computed, never stored. What the ledger actually credits to the budget after applying subject caps against other recordings on the timeline. Recalculated from scratch every time the timeline changes.

`method` and `transmitScalar` serve the UI (showing the player "you transmitted this at 30% efficiency") but do not drive the recalculation math.

### 4.4 Science spending action schema

```
ScienceSpending
  ut:          double — frozen KSC time
  sequence:    int — order of unlock within that UT (0, 1, 2...)
  nodeId:      string — tech tree node ID (e.g. "survivability")
  cost:        float — science points spent
```

### 4.5 Science reservation system

Science uses the same reservation system as funds (section 5.6). All committed science spendings (tech node unlocks) — past, present, and future — are reserved against total effective earnings. The player can only spend what remains after all reservations.

**Available science at any UT:**

```
availableScience(ut) = sum(effective science earnings up to ut) - sum(ALL committed science spendings on entire timeline)
```

Note: this uses `effectiveScience` (post-cap derived values), not raw `scienceAwarded`. The recalculation must run first to compute effective values, then available is derived from those.

**Why this is needed:** without reservation, a player who rewinds to a UT where only some earnings have accumulated could see a science balance that appears sufficient to unlock a new tech node, but existing future spendings already consume that budget.

```
Example without reservation:
  Recording A earns 30 at UT=500. Recording B earns 20 at UT=1000.
  Tech node at UT=1500 costs 45. Balance after walk: 5.

  Player rewinds, fast-forwards to UT=600. Running balance: 30 (only A's earnings).
  Player sees 30 science, unlocks 25-cost node. Allowed.
  Walk: UT=500 +30, UT=600 -25, UT=1000 +20, UT=1500 -45 → -20. DEFICIT.

With reservation:
  Available at UT=600: 30 (earned) - 45 (future tech node) = -15 → 0. Blocked.
```

**At KSC spending time:** the ledger checks `availableScience(currentUT) >= cost` before allowing a new tech node unlock. If insufficient, the unlock is blocked.

**UI patching:** Parsek patches KSP's R&D science balance to show `availableScience(currentUT)` clamped to 0, not the raw running balance. The player sees what they can actually spend. Parsek's own UI can show the full breakdown (earned, reserved, available).

### 4.6 Science recalculation

The timeline can have multiple recordings that collected science from the same subject. Each recording's `scienceAwarded` was computed by KSP in isolation (the player may have rewound to a fresh save state between recordings). The sum of `scienceAwarded` across recordings can exceed the subject's maximum. The recalculation prevents this.

```
RecalculateScienceTimeline():
  1. Reset every subject's creditedTotal to 0.
  2. Reset runningScience to 0.
  3. Collect ALL science actions (earnings + spendings) from ALL committed recordings.
  4. Sort by (UT, type [earning before spending], sequence).
  5. Walk forward:

     For each action in sort order:
       if EARNING:
         subject = SubjectLedger[action.subjectId]
         headroom = subject.maxValue - subject.creditedTotal
         action.effectiveScience = min(action.scienceAwarded, headroom)
         subject.creditedTotal += action.effectiveScience
         runningScience += action.effectiveScience

       if SPENDING:
         action.affordable = (runningScience >= action.cost)
         if action.affordable:
           runningScience -= action.cost
```

Chronological order on the timeline determines priority. Earlier recordings get full credit. Later recordings get the remainder. If a recording is added to the past (player rewinds and commits a new mission), the recalculation gives it priority and reduces later recordings' effective values.

The `affordable` flag on spending actions is defensive — it should never be false in normal play because the reservation system (section 4.5) prevents new spendings from creating a deficit, and the no-delete invariant ensures total effective science never decreases, so existing spendings remain valid. If the flag is false, it indicates a bug or data corruption.

### 4.7 Verified scenarios

**Basic earning and retroactive priority:**

```
Subject: crewReport@MunSrfLandedMidlands, scienceCap = 15

Step 1: Player flies T1 at UT=1000, earns 10. Commits.
  Recalculate: T1 effective = 10. Total = 10.

Step 2: Player rewinds to UT=500. Flies T0 at UT=700, earns 10. Commits.
  Recalculate (sorted by UT):
    T0 at UT=700:  headroom=15, effective=10, credited=10
    T1 at UT=1000: headroom=5,  effective=5,  credited=15
  Total = 15.

Committing T0 retroactively reduced T1's effective from 10 to 5.
Total correctly capped at subject maximum of 15.
```

**Mixed transmit/recover:**

```
Subject scienceCap = 10

Step 1: Fly at UT=1000, transmit. KSP awards 3. Commit.
  UT=1000: headroom=10, effective=3, credited=3. Total=3.

Step 2: Rewind to UT=300, fly at UT=500, transmit. KSP awards 3. Commit.
  UT=500:  headroom=10, effective=3, credited=3
  UT=1000: headroom=7,  effective=3, credited=6. Total=6.

Step 3: Rewind to UT=100, fly at UT=200, recover. KSP awards 10. Commit.
  UT=200:  headroom=10, effective=10, credited=10
  UT=500:  headroom=0,  effective=0,  credited=10
  UT=1000: headroom=0,  effective=0,  credited=10. Total=10.

Recovery at UT=200 consumes the entire cap.
Both transmissions remain on the timeline (scienceAwarded is immutable)
but their effectiveScience drops to 0. Correct.
```

**Earnings with interleaved spending:**

```
Subject scienceCap = 10 for subjectA. Tech node costs 8.

Step 1: Fly at UT=1000, recover subjectA, earn 10. Commit. Balance=10.
Step 2: Unlock tech node at UT=1500, cost 8. Balance=2.
Step 3: Rewind to UT=100, fly at UT=200, recover same subjectA. Commit.
  Walk:
    UT=200:  effective=10, balance=10
    UT=1000: effective=0 (cap hit), balance=10
    UT=1500: affordable (10 >= 8), balance=2.
  Earnings shifted between recordings but total held. Spending still valid.
```

**Reservation blocks new spending on rewind:**

```
Recording A earns 30 science at UT=500. Recording B earns 20 at UT=1000.
Tech node at UT=1500 costs 45. Balance after walk: 5.

Player rewinds, fast-forwards to UT=600.
  Effective earnings up to UT=600: 30 (recording A only).
  ALL committed spendings: 45 (tech node).
  Available: 30 - 45 = -15 → 0.
  Player cannot unlock a new tech node. Correct.

Player fast-forwards to UT=1100.
  Effective earnings up to UT=1100: 50 (both recordings).
  Available: 50 - 45 = 5.
  Player can unlock a node costing up to 5. Correct.
```

### 4.8 Edge cases

**Multiple collections within one recording.** A player transmits an experiment during a mission, then recovers the vessel with the same experiment still onboard (some instruments are rerunnable, or they ran the experiment again after transmitting). KSP treats these as separate `SubmitScienceData` calls with separate awards. The ledger captures them as two `ScienceEarning` rows with the same `subjectId` and `recordingId` but different UTs. The hard-cap walk handles this naturally — they're just two entries in the sort order.

**Hard cap vs KSP's diminishing curve.** The recalculation uses a hard cap (`scienceCap`) rather than simulating KSP's asymptotic diminishing returns curve. This is conservative — it may sometimes credit slightly more than KSP's curve would in a sequential playthrough, but never more than `scienceCap`. Simulating the exact curve would require replacing the clean hard-cap walk with curve simulation across recordings, adding significant complexity for marginal accuracy. The hard cap is correct in the way that matters: no overcredit, no paradox.

**Science Archive (R&D building).** KSP's Research and Development building has a Science Archive tab that displays per-subject progress bars based on `ResearchAndDevelopment.Instance` state. After recalculation, Parsek should patch the per-subject collected totals so the Science Archive reflects the recalculated timeline. **Note (v0.3): per-subject patching is not yet implemented — only the total science balance is patched.** (see section 3.1). Without this patching, the player would see stale progress bars that don't account for retroactive recordings.

---

## 5. Funds Module

**Status:** Designed — seeded balance, reservation system, earning/spending schemas, and verified scenarios complete.

**Game mode applicability:** Not applicable in sandbox (unlimited funds) or Science mode (no funds tracking). Active in Career mode only.

### 5.1 KSP's funds system

Funds are the monetary currency of Career mode. They are a simple linear balance — no diminishing returns curve, no per-subject caps. +50,000 funds is always +50,000, regardless of current balance.

Career mode starts with an initial fund balance (default 25,000 at Normal difficulty, configurable). This is the seed value for the ledger.

### 5.2 Seeded balance

Unlike science (which starts at 0), funds start at a non-zero value determined by the career difficulty settings. The ledger must be seeded with the actual starting funds from the save file — this value varies by difficulty preset (Easy, Normal, Moderate, Hard) and can be customized by the player or mods.

```
FundsInitial (seed — not a player action)
  initialFunds:   float — extracted from the save file at career creation
```

The seed is extracted once when Parsek first initializes on a career save. It is immutable. Parsek must not assume a default value — it reads whatever the save file reports as the career starting funds.

### 5.3 Earning sources

Fund earnings come from multiple first-tier modules:

- **Milestones**: `fundsAwarded` from effective milestone achievements (first-tier, flows into funds walk).
- **Contracts**: completion rewards, advance payments on acceptance.
- **Vessel recovery**: percentage of vessel part costs returned based on distance from KSC. KSP calculates the recovery value; the ledger stores it as immutable.
- **Other**: miscellaneous KSP events that award funds.

All earning values are immutable — what KSP reported at recording time. No curve or diminishing returns.

### 5.4 Spending sinks

- **Vessel construction**: part costs deducted at launch. Recording-associated — tied to a `recordingId`.
- **Facility upgrades**: KSC building level purchases. KSC spending action.
- **Facility repairs**: repair cost after building destruction. KSC spending action.
- **Kerbal hiring**: crew recruitment costs. KSC spending action.
- **Contract penalties**: funds deducted on contract failure or cancellation.
- **Strategy costs**: if applicable (deferred to Strategies module).

### 5.5 Fund action schemas

```
FundsEarning (recording-associated or first-tier-derived)
  ut:             double
  recordingId:    string or NULL — NULL if derived from milestone/contract action
  fundsAwarded:   float — amount earned (IMMUTABLE)
  source:         enum — CONTRACT_COMPLETE | CONTRACT_ADVANCE | RECOVERY |
                         MILESTONE | OTHER

FundsSpending (recording-associated or KSC spending action)
  ut:             double
  sequence:       int — order within same UT (for KSC spendings)
  recordingId:    string or NULL — non-NULL for vessel builds, NULL for KSC spendings
  fundsSpent:     float — amount spent (IMMUTABLE)
  source:         enum — VESSEL_BUILD | FACILITY_UPGRADE | FACILITY_REPAIR |
                         KERBAL_HIRE | CONTRACT_PENALTY | STRATEGY | OTHER

FundsInitial (seed — created once at career start)
  ut:             0 (always)
  initialFunds:   float — extracted from the save file at career creation
```

### 5.6 Reservation system

Funds use a reservation system to prevent overspending when the player rewinds to an earlier UT. All committed spendings on the timeline — past, present, and future — are reserved against the total earnings. The player can only spend what remains after all reservations.

**Available funds at any UT:**

```
availableFunds(ut) = sum(seed + all earnings up to ut) - sum(ALL committed spendings on entire timeline)
```

The key insight: spendings from the future (after the current UT) are included in the reservation. This is analogous to the kerbal reservation system — future committed recordings lock resources retroactively.

**At commit time:** the ledger checks whether the recording's vessel build cost fits within the available budget at launch UT. If `availableFunds(launchUT) >= vesselCost`, the commit is allowed. Otherwise it is blocked — the player must earn more funds, fast-forward to a later UT where more earnings are available, or revert and use a cheaper vessel.

**At KSC spending time:** the ledger checks `availableFunds(currentUT) >= cost` before allowing facility upgrades, kerbal hires, or other KSC purchases.

### 5.7 Why available can appear low or zero at early UTs

After committing multiple recordings, the total committed spendings may exceed the earnings available at an early UT. This is not a bug — each spending was individually valid when committed (available >= cost at the time of commitment). But viewed from an earlier UT where earnings haven't accumulated yet, the available balance is low or zero.

```
Example:
  UT=0:    Seed 25k.
  UT=100:  Earn 50k (milestone). Balance: 75k.
  UT=200:  Spend 30k (vessel). Balance: 45k.
  UT=500:  Earn 40k (contract). Balance: 85k.
  UT=600:  Spend 60k (facility). Balance: 25k.

  Total spendings: 90k. Seed + earnings up to UT=0: 25k.
  Available at UT=0: 25k - 90k = -65k → effectively 0.

  Player rewinds to UT=0. Cannot build anything — budget fully committed.
  Player fast-forwards to UT=150 (after milestone): 75k - 90k = -15k → still 0.
  Player fast-forwards to UT=550 (after contract): 115k - 90k = 25k → can spend.
```

The available value is clamped to 0 in the KSP UI. The player sees "no funds available" rather than a negative number. Parsek's detail panel can show the full breakdown (balance, reserved, available) for players who want to understand why.

### 5.8 Funds recalculation

Funds is a second-tier module. It runs after first-tier modules (Milestones, Contracts) have set their effective flags.

```
RecalculateFunds():
  runningBalance = initialFunds
  totalReserved = sum(ALL committed spendings on timeline)

  For each fund-affecting action in UT order:
    if EARNING:
      if source == MILESTONE:
        check milestone module's effective flag
        if effective: runningBalance += fundsAwarded
      else:
        runningBalance += fundsAwarded

    if SPENDING:
      runningBalance -= fundsSpent
      action.affordable = (runningBalance >= 0)  // defensive check

  // Available at any UT can be derived:
  // availableFunds(ut) = runningBalance(ut) - sum(spendings after ut)
```

The `affordable` flag is defensive. With proper seeding and the reservation system preventing overspending, it should always be true. If it's false, it indicates a bug or data corruption.

**Implementation handles more action types:** Beyond `FundsEarning` and `FundsSpending`, the walk also processes: `ContractAccept` (advance payment), `ContractComplete` (rewards, using TransformedFundsReward, see section 11.4), `ContractFail`/`ContractCancel` (penalties), `FacilityUpgrade`/`FacilityRepair` (costs via FacilityCost field), `KerbalHire` (hiring cost), and `StrategyActivate` (setup cost). All spending-type actions are included in the reservation total.

### 5.9 UI patching

On warp exit / rewind, Parsek patches KSP's fund display:

- `Funding.Instance.Funds` is set to `availableFunds(currentUT)`, not `runningBalance(currentUT)`. The player sees what they can spend, not the gross balance.
- Parsek's own UI shows the breakdown: gross balance, reserved amount (future committed spendings), and available amount.

This means the KSP toolbar funds display always reflects what the player can actually spend on a new vessel or facility. No surprises.

### 5.10 Vessel build cost — when and how

KSP deducts vessel cost at launch. The recording captures this:

- The vessel cost is captured during flight (at launch time).
- On commit, the cost is extracted into the ledger as a `FundsSpending` with `source=VESSEL_BUILD` and the recording's `recordingId`.
- The spending UT is the recording's launch UT.

The player doesn't choose the cost — it's determined by the parts on the vessel. KSP calculates it. The ledger stores it as immutable.

### 5.11 Vessel recovery — earning on return

When a vessel is recovered, KSP returns a percentage of the vessel's part costs based on distance from KSC (closer = higher percentage). The recovery value is:

- Captured by KSP's `OnVesselRecoveryProcessing` callback.
- Captured during the recording.
- Extracted into the ledger as a `FundsEarning` with `source=RECOVERY` on commit.
- The earning UT is the recovery UT.

The recovery percentage formula is KSP's domain — the ledger stores only the final awarded amount.

### 5.12 Verified scenarios

**Basic career with seeded balance:**

```
Seed: 25,000. 

UT=10:  Vessel build -5,000.   Balance: 20,000. ✓
UT=50:  Milestone +8,000.      Balance: 28,000.
UT=60:  Recovery +3,000.       Balance: 31,000.

Available at UT=100: 31,000 - 0 future spendings = 31,000.
Player can spend up to 31,000 on next vessel.
```

**Reservation blocks overspending on rewind:**

```
Seed: 25,000.

UT=10:  Vessel A -5,000.   Balance: 20,000.
UT=50:  Earn +8,000.       Balance: 28,000.
UT=60:  Earn +3,000.       Balance: 31,000.
UT=100: Vessel B -15,000.  Balance: 16,000.
UT=200: Earn +20,000.      Balance: 36,000.
UT=250: Earn +12,000.      Balance: 48,000.

Total spendings: 20,000. Total budget: 73,000.

Player rewinds to UT=50:
  Earnings up to UT=50: 25,000 + 8,000 = 33,000.
  All spendings: 20,000.
  Available: 13,000.
  Can build 10k vessel? Yes.
  Can build 20k vessel? No.
```

**Facility upgrade and kerbal hire interleaved:**

```
Seed: 50,000.

UT=50:  Vessel A -20,000.     Balance: 30,000.
UT=100: Earn +30,000.         Balance: 60,000.
UT=200: Vessel B -15,000.     Balance: 45,000.
UT=300: Earn +25,000.         Balance: 70,000.
UT=400: Vessel C -30,000.     Balance: 40,000.
UT=500: Earn +40,000.         Balance: 80,000.
UT=600: Hire kerbal -25,000.  Balance: 55,000.
UT=700: Facility -35,000.     Balance: 20,000.

Walk: all balances non-negative ✓.
Total spendings: 125,000. Total budget: 195,000.
Available at UT=0: 50,000 - 125,000 = -75,000 → 0.
Available at UT=500: 170,000 - 125,000 = 45,000.
```

**New recording adds earnings, expanding budget:**

```
Seed: 25,000. Existing timeline:
  UT=100: Earn +100,000. UT=500: Vessel -40,000. UT=1500: Facility -50,000.
  Total spendings: 90,000. Budget at UT=200: 125,000. Available at UT=200: 35,000.

Player rewinds to UT=200, commits rec_B (earns 30k contract, vessel costs 15k):
  New spendings total: 105,000. New budget at UT=300: 155,000.
  Available at UT=300: 155,000 - 105,000 = 50,000.
  Net effect: +15,000 available (earned 30k, spent 15k).
```

### 5.13 No-delete invariant for funds

The no-delete invariant provides the same structural guarantee for funds as for other spendable resources:

- **Earnings are monotonically growing.** Adding a recording can only add earnings (and its own vessel cost). Existing earnings are never removed.
- **Spendings can grow.** Each new recording adds a vessel build cost. KSC spendings (facility upgrades, hires) are added independently.
- **Existing spendings are never invalidated.** Adding a recording may redistribute fund earnings (e.g. milestone credit shifting), but the total earned stays the same or increases. Spendings that were valid when committed remain valid.
- **New spendings are gated by the reservation system.** The player cannot add a spending that would create a deficit at any point on the timeline.

This is the same pattern as science (section 4.8). Both resources need the reservation system because the player can add new KSC spendings (tech nodes for science, facility upgrades and vessel builds for funds) that could create deficits without it.

### 5.14 Open questions

- **VAB/SPH editing sessions:** Does the ledger need to track vessel cost changes during editing (parts added/removed), or only the final cost at launch? Only the final cost at launch seems necessary — the ledger doesn't track what happened in the editor.
- **Vessel cost with recoverable parts:** If a vessel is partially recovered (some stages survived), does the recovery value need to account for which parts were recovered? KSP handles this natively — the ledger just stores the awarded recovery amount.
- **Strategy conversions:** Strategies can convert reputation to funds as an ongoing rate. Deferred to the Strategies module.

---

## 6. Reputation Module

**Status:** Designed — schema, curve-dependent recalculation model, and dependency on first-tier modules specified. Exact curve formula TBD (requires decompilation).

**Game mode applicability:** Not applicable in sandbox or Science mode. Active in Career mode only.

### 6.1 KSP's reputation system

Reputation is a global score ranging from approximately -1000 to +1000. It affects gameplay in several ways:

- Higher reputation increases contract difficulty and rewards.
- More contract slots become available at higher rep.
- Strategy availability in the Administration building requires minimum rep thresholds.
- Low/negative rep can create a death spiral — bad contracts, low funds, hard to recover.

**Non-linear gain/loss curve.** KSP does not add reputation linearly. Gains diminish as reputation approaches the upper limit (1000) — a "+50 rep" reward might only add 45 at low rep but only 5 at high rep. Losses are asymmetrically harsher — the same magnitude of penalty has a larger impact than a gain. KSP's `AddReputation` method implements this by splitting amounts into small "grains" and tracing the curve incrementally.

**The exact curve formula is not publicly documented.** The coding agent should decompile KSP's `Reputation` class (specifically `AddReputation` and the grain system) to extract the precise formula. Parsek must replicate this formula in the recalculation walk to produce correct per-event effective values.

### 6.2 Why the curve matters for recalculation

Unlike science (where `scienceAwarded` is the post-curve actual value), reputation rewards are context-dependent — the same nominal "+50 rep" produces different actual gains depending on the current rep level. When the timeline changes and events shift order, the running rep at each event's UT changes, which changes the effective rep for every subsequent event.

If the ledger only stored and summed nominal values, per-event effective values would be wrong. The player couldn't see "this milestone actually gave you 5 rep" vs "this one gave you 45 rep." The walk must simulate the curve to produce correct intermediate results.

### 6.3 Immutable vs derived values

**`nominalRep`** — immutable. The reward or penalty amount as defined by KSP's contract/milestone configuration. Written once, never changes. This is the number KSP would display as the reward before applying the curve.

**`effectiveRep`** — derived. The actual reputation change after applying the gain/loss curve against the running rep at that UT. Recalculated from scratch every time the timeline changes.

### 6.4 Reputation action schemas

```
ReputationEarning (recording-associated or first-tier-derived)
  ut:             double
  recordingId:    string or NULL — NULL if derived from a milestone/contract action
  nominalRep:     float — reward amount before curve (IMMUTABLE)
  source:         enum — CONTRACT_COMPLETE | MILESTONE | OTHER

ReputationPenalty (recording-associated or spending action)
  ut:             double
  recordingId:    string or NULL — NULL for KSC actions (contract decline)
  nominalPenalty: float — penalty amount before curve (IMMUTABLE)
  source:         enum — CONTRACT_FAIL | CONTRACT_DECLINE | KERBAL_DEATH | STRATEGY | OTHER
```

Note: `KERBAL_DEATH` is a recording-associated penalty — killing kerbals costs reputation. This connects the kerbals module to the reputation module.

Milestone and contract rep values flow in from first-tier modules. The reputation module does not create separate entries for these — it reads the `repAwarded` fields on `MilestoneAchievement` and contract actions, checks their `effective` flags, and applies the curve.

### 6.5 Reputation recalculation

Reputation is a second-tier module. It runs after first-tier modules (Milestones, Contracts) have set their effective flags.

```
RecalculateReputation():
  runningRep = 0
  For each rep-affecting action in UT order:

    if MILESTONE (from milestones module):
      if action.effective:
        effectiveGain = applyGainCurve(action.repAwarded, runningRep)
        runningRep += effectiveGain

    if CONTRACT_COMPLETE (from contracts module):
      if action.effective:
        effectiveGain = applyGainCurve(action.nominalRep, runningRep)
        action.effectiveRep = effectiveGain
        runningRep += effectiveGain

    if PENALTY (contract fail, kerbal death, etc.):
      effectiveLoss = applyLossCurve(action.nominalPenalty, runningRep)
      action.effectivePenalty = effectiveLoss
      runningRep -= effectiveLoss
```

`applyGainCurve(nominal, currentRep)` and `applyLossCurve(nominal, currentRep)` must replicate KSP's internal curve. These are the functions that need to be extracted from decompiled code.

### 6.6 Patching

On warp exit / rewind, Parsek sets `Reputation.Instance.reputation` to `runningRep` at the current UT. Since the recalculation simulates the curve, the patched value should match what KSP would have computed natively.

### 6.7 Verified scenario

```
Assume simple gain curve: effectiveGain = nominal × (1 - currentRep / 1000)

Step 1: Recording A at UT=1000 completes contract, nominal +50 rep. Commit.
  runningRep = 0. effectiveGain = 50 × (1 - 0/1000) = 50. runningRep = 50.

Step 2: Recording A also achieves "First Mun Landing," nominal +15 rep.
  runningRep = 50. effectiveGain = 15 × (1 - 50/1000) = 14.25. runningRep = 64.25.

Step 3: Rewind to UT=500. Recording B at UT=700 achieves "First Mun Landing." Commit.
  Recalculate:
    UT=700 (B milestone):  runningRep=0. effective=true.
      effectiveGain = 15 × (1 - 0/1000) = 15. runningRep = 15.
    UT=1000 (A contract):  runningRep=15.
      effectiveGain = 50 × (1 - 15/1000) = 49.25. runningRep = 64.25.
    UT=1000 (A milestone): effective=false (already credited to B). +0.

  Total rep = 64.25 (was 64.25 before — close but not identical due to curve reordering).
```

Note: the total can shift slightly when events reorder because the curve is non-linear. This is correct behavior — the curve depends on order, and the timeline determines order.

### 6.8 Order-dependent totals and the no-delete invariant

Unlike science (where subject caps make totals stable regardless of event order), reputation totals are order-dependent due to the non-linear curve. Reordering events by adding a retroactive recording can shift the final rep value slightly, because the curve produces different effective gains at different running rep levels.

This is expected and harmless. There is no ledger-enforced invariant that depends on a minimum or exact reputation value (unlike funds, where spending affordability must be validated). Reputation affects contract offerings and strategy availability, but those are KSP-native checks at the current moment — not constraints the ledger enforces during the walk.

The no-delete invariant still holds in a weaker sense: rep-affecting events are never removed from the timeline (only added), so the set of inputs to the curve is monotonically growing. But the output of the curve (final rep) is not monotonically stable — it can shift slightly with each retroactive commit.

### 6.9 ~~Open questions~~ Resolved

- **Exact curve formula:** RESOLVED. Extracted from decompiled `Reputation` class. Uses `addReputation_granular` with Hermite spline AnimationCurve. 5-key addition curve (gain ~2x at rep=-1000, ~1x at rep=0, ~0x at rep=+1000) and 4-key subtraction curve (loss ~0x at rep=-1000, ~1x at rep=0, ~2x at rep=+1000). Implemented in `ReputationModule.ApplyReputationCurve` with the exact keyframes. See `docs/dev/plans/game-actions-spike-findings.md` for full keyframe data.
- **Reputation floor:** Loss curve multiplier approaches ~0.0x near rep=-1000 (symmetric with gain ceiling). No hard clamp in `AddReputation`, but `SetReputation` clamps to [-1000, 1000].

---

## 7. Milestones Module

**Status:** Designed — schema, recalculation logic, and verified scenarios complete.

**Game mode applicability:** Not applicable in sandbox (no progression tracking). In Science mode, milestones are tracked by KSP's Progress Tracking system but award no funds or reputation (those resources don't exist in Science mode). In Career mode, milestones award funds and reputation. The ledger tracks milestones in both Science and Career modes — in Science mode for progression gating, in Career mode for progression gating and resource awards.

### 7.1 KSP's milestone system

KSP's Progress Tracking / World Firsts system fires achievements for spaceflight firsts: first orbit of Kerbin, first Mun landing, first EVA on Duna, etc. Each milestone fires once ever per save. In Career mode, the World Firsts contract strategy automatically awards funds and reputation for each milestone.

Milestones are triggered automatically during flight by game events. The player does not choose to achieve them — they happen as a consequence of what the vessel does.

### 7.2 Milestone action schema

```
MilestoneAchievement (recording-associated action)
  ut:             double — when it was achieved during flight
  recordingId:    string — which recording triggered it
  milestoneId:    string — e.g. "FirstOrbitKerbin" / "FirstMunLanding"
  fundsAwarded:   float — what KSP awarded (IMMUTABLE, career only, 0 in Science mode)
  repAwarded:     float — what KSP awarded (IMMUTABLE, career only, 0 in Science mode)
```

`fundsAwarded` and `repAwarded` are immutable — written once at recording time based on KSP's milestone reward configuration. They are 0 in Science mode.

`effective` is derived — recalculated every time. True for the chronologically first recording that achieved this milestone, false for all later duplicates.

There is no spending side. Milestones are earn-only.

### 7.3 Milestone recalculation

The milestone walk is a binary cap — each milestone can be credited exactly once. This is the same pattern as science subject caps, but simpler: headroom is either 1 (not yet achieved) or 0 (already achieved).

```
RecalculateMilestones():
  1. Reset all milestone flags to uncredited.
  2. Collect all MilestoneAchievement actions.
  3. Sort by UT (within same UT, order doesn't matter — 
     a recording can't trigger the same milestone twice).
  4. Walk forward:

     For each action in UT order:
       if milestoneId not yet credited:
         action.effective = true
         mark milestoneId as credited
       else:
         action.effective = false
```

This is a first-tier walk. Once effective flags are set, the milestone's `fundsAwarded` and `repAwarded` values flow into the Funds and Reputation modules' running balances in the second tier of the same recalculation pass.

### 7.4 Verified scenarios

**Basic milestone achievement:**

```
Recording A at UT=1000 achieves "First Mun Landing." Commit.
  Recalculate: effective=true. +10000 funds, +15 rep.
```

**Retroactive priority shift:**

```
Step 1: Recording A at UT=1000 achieves "First Mun Landing." Commit.
  effective=true. +10000 funds, +15 rep.

Step 2: Rewind to UT=500. Recording B at UT=700 also achieves "First Mun Landing." Commit.
  Recalculate:
    UT=700 (B):  not yet credited → effective=true. +10000 funds, +15 rep.
    UT=1000 (A): already credited → effective=false. +0 funds, +0 rep.
  Credit shifted from A to B. Total unchanged.
```

**Multiple milestones across recordings:**

```
Recording A at UT=1000: "First Mun Landing" + "First Mun EVA." Commit.
  Both effective. +10000 funds +15 rep, +5000 funds +8 rep.

Rewind to UT=500. Recording B at UT=700: "First Mun Landing" only. Commit.
  Recalculate:
    UT=700 (B):  "First Mun Landing" → effective.
    UT=1000 (A): "First Mun Landing" → NOT effective. "First Mun EVA" → effective.
  B took the landing credit. A keeps the EVA credit. Total funds and rep unchanged.
```

### 7.5 No-delete invariant for milestones

The same no-delete safety applies: adding a recording can only shift which recording gets credit for a milestone, never reduce the total number of milestones achieved. If milestone X was effective on any recording before, it's still effective on some recording after adding a new one. Total milestone rewards are monotonically stable.

### 7.6 Open questions

- **Milestone detection during recording:** How does Parsek capture milestone triggers? KSP fires `OnProgressComplete` events during flight. The sidecar captures these, and the ledger extracts milestone data on commit — same pattern as science.
- **Science mode milestones:** In Science mode, milestones track progression but award nothing. Does the ledger need to track them at all, or is KSP's native Progress Tracking sufficient? If milestones gate contract availability (even indirectly), the ledger may need them for consistency.
- **Milestone list:** The ledger only tracks achieved milestones. It does not maintain a list of all possible milestones — that's KSP's domain.

---

## 8. Contracts Module

**Status:** Designed — state transitions, reservation system, once-ever completion, deadline handling, and KSP state patching specified.

**Game mode applicability:** Not applicable in sandbox or Science mode. Active in Career mode only. Contracts are the primary source of funds and reputation income in career.

### 8.1 KSP's contract system

Contracts are procedurally generated offers that reward the player for completing objectives. Each contract has a state machine:

```
OFFERED → ACCEPTED → COMPLETED
                   → FAILED (deadline expiration or vessel loss)
                   → CANCELLED (player choice at KSC)
```

Each transition has resource effects:

- **Accept**: advance payment (funds earning), contract slot consumed.
- **Complete**: reward (funds + reputation + sometimes science earning).
- **Fail**: penalty (funds + reputation loss).
- **Cancel**: partial penalty (funds + reputation loss).

Contracts are offered by KSP procedurally based on progress, reputation level, and randomness. Mission Control has a limited number of active contract slots (determined by building level). Each contract has a unique ID assigned by KSP.

### 8.2 Contract reservation — UT=0 to resolution

Contracts follow the same reservation pattern as kerbals: once accepted anywhere on the timeline, a contract is reserved from UT=0 until it resolves (completed, failed, or cancelled). This means:

- A contract accepted at UT=1000 consumes a Mission Control slot from UT=0.
- At any rewind point before UT=1000, the slot is still consumed.
- The contract shows as "accepted" in Mission Control at all UTs, even before its accept UT.
- The slot is freed when the contract resolves.
- An unresolved contract (no completion, failure, or cancellation on the timeline) reserves the slot indefinitely.

```
ContractReservation (derived, per contract)
  contractId:     string
  reservedFrom:   0 (always — invariant)
  reservedUntil:  resolution UT (complete/fail/cancel), or INDEFINITE if unresolved
```

**Slot availability at any UT:**

```
activeContracts = count of contracts that are reserved and unresolved at current UT,
                  OR resolved but resolution UT is after current UT
availableSlots(ut) = maxSlots (from Mission Control level) - activeContracts
```

This prevents the player from over-accepting contracts after a rewind. Future-accepted contracts hold their slots.

### 8.3 Once-ever completion (like milestones)

A specific contract instance can only be completed once on the timeline. If two recordings both meet the completion conditions for the same contract, only the chronologically first one gets credit.

```
For each ContractComplete in UT order:
  if contractId not yet credited:
    action.effective = true
    mark contractId as credited
  else:
    action.effective = false  // duplicate completion, rewards zeroed
```

Effective completion rewards (funds, rep, science) flow into second-tier modules. Non-effective completions produce zero rewards but the contract is still resolved (slot freed).

### 8.4 Contract action schemas

```
ContractAccept (KSC action — consumes a slot)
  ut:             double — frozen KSC time
  sequence:       int — order within same UT
  contractId:     string — KSP's unique contract instance ID
  contractType:   string — e.g. "ExploreBody", "PartTest", "TourismContract"
  title:          string — human-readable (e.g. "Explore the Mun")
  advanceFunds:   float — advance payment received (IMMUTABLE)
  deadlineUT:     float or NULL — expiration UT, NULL if no deadline

ContractComplete (recording-associated earning)
  ut:             double — when completion conditions were met during flight
  recordingId:    string
  contractId:     string
  fundsReward:    float — IMMUTABLE
  repReward:      float — IMMUTABLE (nominal, pre-curve)
  scienceReward:  float — IMMUTABLE (some contracts award science)

ContractFail (recording-associated or timeline event)
  ut:             double — when failure occurred
  recordingId:    string or NULL — NULL if deadline expiration
  contractId:     string
  fundsPenalty:   float — IMMUTABLE
  repPenalty:     float — IMMUTABLE (nominal, pre-curve)

ContractCancel (KSC action)
  ut:             double — frozen KSC time
  sequence:       int
  contractId:     string
  fundsPenalty:   float — IMMUTABLE
  repPenalty:     float — IMMUTABLE (nominal, pre-curve)
```

### 8.5 Contract recalculation

Contracts are a first-tier module. The walk processes contract actions and sets effective flags. Fund and reputation effects flow into second-tier modules.

```
RecalculateContracts():
  creditedContracts = {}
  activeContracts = {}

  For each contract action in UT order:
    if ACCEPT:
      activeContracts[contractId] = action
      // advance funds flow into Funds module

    if COMPLETE:
      if contractId not in creditedContracts:
        action.effective = true
        creditedContracts[contractId] = true
        // rewards flow into Funds, Reputation, Science modules
      else:
        action.effective = false
      remove contractId from activeContracts (slot freed)

    if FAIL:
      // penalties flow into Funds, Reputation modules
      remove contractId from activeContracts (slot freed)

    if CANCEL:
      // penalties flow into Funds, Reputation modules
      remove contractId from activeContracts (slot freed)
```

### 8.6 Deadline handling

**Status: DEFERRED.** Deadline failure generation is not implemented. The code has a TODO comment.

Contracts with deadlines generate an automatic failure event when the deadline UT is crossed. During the recalculation walk, if an accepted contract's deadline UT is reached without a prior completion or cancellation, the walk inserts a `ContractFail` event at the deadline UT.

```
During walk, for each accepted contract:
  if deadlineUT is reached AND contract still in activeContracts:
    generate ContractFail at deadlineUT (recordingId = NULL)
    apply penalties
    remove from activeContracts
```

This means deadline failures are derived events — they don't need to be stored in the ledger. They're produced during the walk whenever an accepted contract's deadline passes without resolution.

During fast-forward, the deadline crossing is visual only — the actual failure is processed on warp exit during recalculation.

### 8.7 Funds reservation interaction

Contract advance payments are fund earnings — they increase the balance on accept. Contract rewards are fund earnings on completion. Contract penalties are fund losses on failure/cancel.

All of these participate in the funds reservation system (section 5.6). The advance payment increases available funds at the accept UT. Future rewards are not counted until the recording that completes the contract is committed (earnings are recording-associated).

### 8.8 KSP state patching

On warp exit / rewind, Parsek patches KSP's `ContractSystem` to match the ledger's contract state at the current UT:

- **Accepted contracts**: patched into KSP as active, regardless of whether their accept UT has been reached. They are reserved from UT=0.
- **Completed contracts**: patched as completed. KSP won't re-offer them.
- **Failed contracts**: patched as failed.
- **Cancelled contracts**: patched as cancelled.
- **Contracts not yet on the timeline**: left to KSP's procedural generation. Parsek doesn't interfere with offerings.

This is the most complex patching operation across all modules. KSP's `Contract` objects have parameters, conditions, and internal state that must be correctly restored. The coding agent should investigate KSP's contract serialization API to determine how much of the contract state can be preserved and restored.

### 8.9 Procedural generation across rewinds

KSP generates contract offerings procedurally. After a rewind, KSP may generate different offerings than before. Parsek handles this by patching accepted/completed/failed contracts into KSP's state — these are removed from the offering pool. New offerings are KSP's domain.

This means: if the player accepted "Orbit Mun" in a previous commit, it won't be re-offered after rewind because Parsek patches it as accepted. KSP generates other contracts instead. The player doesn't see duplicate contracts.

### 8.10 Verified scenarios

**Basic accept and complete:**

```
UT=100: Accept "Orbit Mun". Advance +5k funds. Slot consumed.
UT=500: Recording A completes "Orbit Mun". +40k funds, +15 rep. Commit.
  Slot freed. Contract resolved.
```

**Once-ever completion with retroactive priority:**

```
UT=500: Accept "Orbit Mun".
UT=1000: Recording A completes it. effective=true. +40k, +15 rep.

Rewind to UT=600. Recording B also completes "Orbit Mun" at UT=700. Commit.
  Recalculate:
    UT=700 (B): effective=true. +40k, +15 rep.
    UT=1000 (A): effective=false. +0, +0.
  Credit shifts to B. Total unchanged. Contract resolved at UT=700.
```

**Slot reservation across rewind:**

```
Mission Control: 3 slots max.

Contract A: accepted UT=100, completed UT=500.
Contract B: accepted UT=200, pending (no resolution).
Contract C: accepted UT=300, cancelled UT=400.

Player rewinds to UT=50.
  All three reserved from UT=0.
  A resolved at UT=500, C resolved at UT=400, B unresolved.
  Active at UT=50: A (resolved UT=500 > 50), B (unresolved), C (resolved UT=400 > 50) = 3.
  Available slots: 3 - 3 = 0. Player can't accept new contracts.

Player fast-forwards to UT=450.
  C resolved at UT=400 (cancelled). A still active (resolved UT=500 > 450). B unresolved.
  Active: 2 (A, B).
  Available slots: 3 - 2 = 1. Player can accept one more.
```

**Deadline failure during walk:**

```
Contract D: accepted UT=100, deadline UT=800. No completion recorded.

Recalculate walk:
  UT=100: Accept. Slot consumed.
  UT=800: Deadline reached, contract still active → derived ContractFail.
    Apply penalties. Slot freed.
```

### 8.11 Open questions

- **Contract parameter preservation:** KSP contracts have complex internal state (completion conditions, parameters, waypoints). How much of this can Parsek serialize and restore on patching? If contract objects can't be fully reconstructed, Parsek may need to store a serialized snapshot of the contract at accept time.
- **Contract generation seeding:** KSP's procedural contract generation uses randomness. After a rewind, different contracts may be offered. Does Parsek need to seed the RNG to produce consistent offerings, or is divergence acceptable?
- **Contracts that reference recordings:** Some contracts require specific conditions (reach orbit, land on body) that are met during recordings. If a recording is completed but the contract completion is tied to it, and then a second recording also meets the conditions, the once-ever flag handles the duplicate — but does the contract's internal condition-tracking agree with Parsek's state?
- **Tourist contracts:** Tourist contracts place passenger kerbals on the vessel. These kerbals are temporary — they leave after recovery. The ledger does not need to track tourists as managed kerbals (no reservation, no replacement chain). They are purely a contract concern.
- **Rescue contracts:** Rescue contracts spawn a stranded kerbal. Completing the rescue adds them to the roster for free. This overlaps with the kerbals module's `KerbalRescue` action (section 9.10). The contract module records the contract completion; the kerbals module records the kerbal's addition to the roster. Both are recording-associated actions on the same recording.

---

## 9. Kerbals Module

**Status:** Designed — core reservation and replacement systems specified.

Kerbals are unique named entities with identity, state, and scarcity. They are the most complex resource module because they combine reservation (exclusive assignment), replacement (roster stability), and lifecycle management (XP, death, rescue).

**Game mode applicability:** Active in all game modes. In sandbox, kerbals are free and abundant but still have identity — the reservation system prevents the duplicate-kerbal paradox. Replacement is trivial (free, unlimited). In Science mode, kerbals exist and can die/be stranded, but there is no hiring cost (funds don't exist). In Career mode, kerbals cost funds to hire, gain XP, and are scarce — the full reservation, replacement chain, and hiring cost systems apply.

### 9.1 The duplicate kerbal problem

Parsek's rewind creates a unique problem with kerbals. Unlike science (fungible points) or funds (a number), kerbals have identity. The same kerbal cannot physically exist in two places at the same time on the timeline.

```
Without reservation:
  Recording A: Jeb launches UT=1000, stranded on Mun. Committed.
  Player rewinds to UT=500. KSP restores Jeb to roster.
  Recording B: Jeb launches UT=800, flies until UT=1500. Committed.
  Fast-forward: at UT=1000 ghost-Jeb becomes real (stranded on Mun).
  At UT=800–1500 recording-B Jeb is also real and flying.
  Two real Jebs exist from UT=1000 to UT=1500. Paradox.
```

The reservation system prevents this by construction.

### 9.2 Reservation rules

When a recording is committed with a kerbal onboard, that kerbal is **reserved from UT=0 until their latest endpoint across all recordings on the timeline.** The reservation starts at UT=0, not at the recording's launch time. This prevents any earlier mission from using the kerbal.

```
KerbalReservation (derived, per kerbal)
  kerbalName:     "Jeb"
  reservedFrom:   0 (always — invariant)
  reservedUntil:  max(endUT) across all recordings with this kerbal,
                  or INDEFINITE if any recording has DEAD, MIA, or STRANDED end state
                  (STRANDED becomes bounded when a rescue recording provides endUT)
```

The reservation is a single continuous block. There are no gaps. A kerbal is either free (after `reservedUntil`) or completely unavailable (UT=0 through `reservedUntil`). This eliminates any possibility of overlapping assignments.

**Why not gap-based reservation?** An alternative design would allow a kerbal to be used in gaps between recordings (e.g. Jeb recovers at UT=1500, next mission starts at UT=2000 — gap from 1500 to 2000 could be usable). This was rejected because the player cannot control mission duration at launch time. If a player launches Jeb at UT=1600 intending to return by UT=1900, but the mission runs long past UT=2000, it overlaps with the next recording. Enforcement would have to happen at commit time — after the player already flew the entire mission — which is unacceptable UX. The continuous block from UT=0 prevents this by making the kerbal unavailable until all recordings are resolved.

**At commit time:** the ledger checks whether each kerbal on the recording is currently reserved. If so, the commit is rejected — the player must use a different kerbal or revert.

**On rewind:** all reservations are recomputed from scratch, just like science recalculation. Walk all committed recordings, compute reservations per kerbal. Reservation status is always derived, never stored.

### 9.3 End states and reservation duration

| End state | Reservation | Duration | Meaning |
|-----------|-------------|----------|---------|
| RECOVERED | Temporary | UT=0 → recovery UT | Kerbal returned to KSC, available after recovery |
| STRANDED  | Temporary (open-ended) | UT=0 → until rescue recording closes it | Kerbal alive but stuck, rescuable by future mission |
| DEAD      | Permanent | UT=0 → INDEFINITE | Kerbal gone forever |
| MIA       | Permanent | UT=0 → INDEFINITE | Kerbal gone, KSP may make available for rehire later |

STRANDED is temporary in principle even if long-lived in practice. The reservation stays open until a future recording rescues the kerbal (picks them up and recovers). If no rescue ever happens, it behaves like permanent reservation.

**Implementation divergence:** The actual `KerbalEndState` enum uses different values:
- `Aboard = 0` — kerbal is on a vessel that is still intact (covers design doc's STRANDED + active vessel states)
- `Dead = 1` — kerbal was killed
- `Recovered = 2` — kerbal was recovered with the vessel
- `Unknown = 3` — end state could not be determined (legacy recording, missing data; covers design doc's MIA)

The design doc's `STRANDED` is mapped to `Aboard` (crew still on vessel, open-ended reservation). `MIA` is mapped to `Unknown` (conservative: treated as open-ended temporary reservation).

### 9.4 Replacement system — temporary reservations

When a kerbal is temporarily reserved (RECOVERED or STRANDED), the roster would shrink without intervention. Parsek auto-generates a **free stand-in kerbal** to fill the slot.

Stand-in properties:
- Same class as the reserved kerbal (pilot/engineer/scientist).
- **Randomized** attributes (courage, stupidity, name). Not cloned from the original — this prevents roster homogenization over time.
- 0 XP (level 0), as with any new kerbal.
- Free — no funds cost. The reservation is a Parsek artifact, not a player choice, so Parsek covers the cost.

The stand-in exists for the duration of the reservation. When the original kerbal's reservation ends (recovered, or rescued from stranding), the original reclaims their slot:

- If the stand-in was **never used** in any recording → **deleted** from roster.
- If the stand-in **was used** in a recording → **retired** (unassignable, but remains in KSP's roster so recordings referencing them stay valid).

### 9.5 Replacement system — permanent reservations

When a **slot owner** is permanently reserved (DEAD or MIA), Parsek does **not** auto-generate a replacement. The roster shrinks by one. The player must manually hire a new kerbal at KSC, at standard hiring cost, as a normal spending action on the timeline.

The hired kerbal is their own entity — not part of any replacement chain, just a new roster member. This matches vanilla KSP behavior: death has real consequences, the player pays to rebuild their crew.

**Important distinction:** if a **stand-in** dies within a temporary chain (the slot owner is temporarily reserved and will return), the chain continues. A new stand-in is generated to keep the slot filled, because the slot is still needed — the original will eventually reclaim it. The "no auto-replacement" rule only applies when the slot owner themselves is permanently gone.

```
Slot owner dies → no auto-replacement, roster shrinks, player hires manually.
Stand-in dies within temporary chain → chain continues, new stand-in generated.
```

**Sandbox mode:** kerbals are free and the player can create new ones at will. Permanent loss has no mechanical cost — the player simply creates a replacement.

**Science mode:** funds don't exist, so hiring cost doesn't apply. Permanent loss behaves like sandbox — the player creates a replacement kerbal for free. The roster shrink is cosmetic until the player acts.

**Career mode:** the full permanent-loss mechanics apply — roster shrinks, player pays hiring cost to replace.

### 9.6 Replacement chains

Temporary replacements form a per-slot chain. Each original kerbal owns a roster slot. Stand-ins fill the slot when the original is reserved.

```
Slot example:
  Owner: Jeb (Pilot)
  Chain: [Hanley, Kirrim]

  Jeb reserved UT=0→2000 (recording A, recovered).
  Hanley generated to fill slot. Free.
  Hanley reserved UT=0→800 (recording B, recovered).
  Kirrim generated to fill slot. Free.

  Active occupant at UT=0: Kirrim (deepest in chain, both above reserved).
  At UT=800: Hanley reclaims (Kirrim deleted if unused, retired if used).
  At UT=2000: Jeb reclaims (Hanley deleted if unused, retired if used).
```

Chain rules:
- Chains are per-slot, ordered by generation.
- The active occupant is always the deepest free kerbal in the chain.
- When a kerbal reclaims their slot, all deeper stand-ins are displaced (deleted or retired).
- Chains only exist for temporary reservations. Permanent loss exits the chain system.

### 9.7 Retired pool

Retired kerbals are stand-ins that were used in recordings and then displaced when their predecessor reclaimed the slot. They have status RETIRED:

- Remain in KSP's roster (recording reference integrity).
- Unassignable to new missions by Parsek.
- Do not count toward active roster size.

Retired status is **derived, not stored**. On rewind, all kerbal state is recomputed from scratch. A kerbal that was retired at UT=3000 may be reactivated as RESERVED at UT=500 if the timeline still has recordings using them.

The retired pool only grows from temporary-reservation stand-ins that were used in recordings. In practice, this means the pool grows slowly — only when a player uses a stand-in in a mission and the original later returns.

### 9.8 Kerbal action schemas

```
KerbalAssignment (recording-associated action — per kerbal per recording)
  recordingId:    string
  kerbalName:     string
  role:           PILOT | ENGINEER | SCIENTIST
  startUT:        float — mission start
  endUT:          float or NULL — recovery/death/MIA UT, NULL if stranded
  endState:       RECOVERED | DEAD | MIA | STRANDED
  xpGained:       float — XP earned during this recording

KerbalHire (spending action — career only)
  ut:             double — frozen KSC time
  sequence:       int — order within that UT
  kerbalName:     string
  cost:           float — funds spent
  role:           PILOT | ENGINEER | SCIENTIST

KerbalRescue (recording-associated action)
  recordingId:    string — the recording that rescued this kerbal
  kerbalName:     string — the rescued kerbal's name
  role:           PILOT | ENGINEER | SCIENTIST
  recoveryUT:     float — when the rescue recording recovered

KerbalStandIn (generated by Parsek, not a player action)
  kerbalName:     string — procedurally generated
  role:           PILOT | ENGINEER | SCIENTIST
  replacesKerbal: string — who this stand-in fills for
  courage:        float — randomized
  stupidity:      float — randomized
```

### 9.9 XP accumulation

KSP's XP system is achievement-based: a kerbal earns XP for flyby, orbit, landing, and flag planting on each celestial body, but only once per body per achievement type. Repeat visits to the same body yield no additional XP. This deduplication is handled natively by KSP — the ledger stores whatever KSP reported as `xpGained`, same pattern as `scienceAwarded`.

**XP banks on recovery only.** A kerbal must return to Kerbin for XP to be credited. A stranded kerbal accumulates achievements during their mission but does not receive the XP until a rescue recording recovers them. The `xpGained` field on a `KerbalAssignment` with endState=STRANDED is 0 until a subsequent rescue recording resolves it.

Because the reservation system enforces strictly sequential usage (no overlapping missions), XP accumulates in chronological order with no conflicts. The ledger walks all `KerbalAssignment` entries for a kerbal, sorted by UT, and sums `xpGained`. The final XP is the total across all recordings.

### 9.10 Kerbal existence timeline

Not all kerbals exist from UT=0:

- **Default crew** (Jeb, Bill, Val, Bob): exist from UT=0.
- **Hired kerbals**: exist from their hire UT onward.
- **Rescued kerbals**: exist from the recovery UT of the recording that rescued them. Rescue contracts spawn a stranded kerbal in orbit or on a surface; completing the rescue and recovering adds them to the roster for free (no hiring cost). This is a recording-associated action — the rescued kerbal's appearance is tied to the recording that brought them home.
- **Stand-in kerbals**: exist from the moment they're generated (the commit that triggered the reservation).

A kerbal cannot be assigned to a recording that starts before their existence UT. This is naturally enforced: hired kerbals don't appear in the roster until after the hire action, rescued kerbals don't appear until their rescue recording recovers, and stand-ins don't appear until they're generated.

### 9.11 Verified scenarios

**Simple reservation and return:**

```
Starting roster: Jeb (P), Bill (E), Val (P), Bob (S). Size = 4.

COMMIT Recording A: Jeb, UT=1000→2000 (recovered).
  Jeb reserved UT=0→2000. Generate Hanley (P, random attrs). Free.
  Active: [Hanley, Bill, Val, Bob] = 4 ✓

FAST-FORWARD to UT=2100:
  Jeb free. Reclaims slot. Hanley unused → DELETE.
  Active: [Jeb, Bill, Val, Bob] = 4 ✓
```

**Deep chain, all stand-ins used:**

```
COMMIT Recording A: Jeb, UT=1000→3000 (recovered).
  Jeb reserved. Generate Hanley (P).
  Active: [Hanley, Bill, Val, Bob] = 4 ✓

COMMIT Recording B: Hanley, UT=500→2500 (recovered).
  Hanley reserved. Generate Kirrim (P).
  Active: [Kirrim, Bill, Val, Bob] = 4 ✓

COMMIT Recording C: Kirrim, UT=200→2000 (recovered).
  Kirrim reserved. Generate Dunford (P).
  Active: [Dunford, Bill, Val, Bob] = 4 ✓

FAST-FORWARD to UT=2100:
  Kirrim free. Reclaims from Dunford. Dunford unused → DELETE.
  Active: [Kirrim, Bill, Val, Bob] = 4 ✓

FAST-FORWARD to UT=2600:
  Hanley free. Reclaims from Kirrim. Kirrim used → RETIRED.
  Active: [Hanley, Bill, Val, Bob] = 4 ✓

FAST-FORWARD to UT=3100:
  Jeb free. Reclaims from Hanley. Hanley used → RETIRED.
  Active: [Jeb, Bill, Val, Bob] = 4 ✓
  Retired: [Kirrim, Hanley]
```

**Stranded then rescued:**

```
COMMIT Recording A: Jeb, UT=1000→STRANDED on Mun.
  Jeb reserved UT=0→INDEFINITE (open-ended temporary).
  Generate Hanley (P). Free.
  Active: [Hanley, Bill, Val, Bob] = 4 ✓

COMMIT Recording B: Rescue mission, picks up Jeb at UT=3000, recovers UT=3500.
  Jeb's reservation updated: UT=0→3500.
  Active: [Hanley, Bill, Val, Bob] = 4 ✓ (Jeb still reserved until 3500)

FAST-FORWARD to UT=3600:
  Jeb free. Reclaims from Hanley. Hanley used → RETIRED.
  Active: [Jeb, Bill, Val, Bob] = 4 ✓
```

**Rewind recomputes everything:**

```
State at UT=3100 from deep chain scenario:
  Active: [Jeb, Bill, Val, Bob] = 4
  Retired: [Kirrim, Hanley]

REWIND to UT=500:
  Recalculate all reservations from ledger:
    Jeb: reserved UT=0→3000 (recording A)
    Hanley: reserved UT=0→2500 (recording B)
    Kirrim: reserved UT=0→2000 (recording C)
  All three reserved at UT=500. Need stand-in.
  Hanley reactivated as RESERVED (was retired).
  Kirrim reactivated as RESERVED (was retired).
  Generate new Dunford (P) for active slot.
  Active: [Dunford, Bill, Val, Bob] = 4 ✓
  Retired: [] (all reserved, none retired at this UT)
```

**Multi-crew mission:**

```
COMMIT Recording A: Jeb + Bill, UT=1000→2000 (both recovered).
  Jeb reserved UT=0→2000. Generate Hanley (P).
  Bill reserved UT=0→2000. Generate Derrick (E).
  Active: [Hanley, Derrick, Val, Bob] = 4 ✓

FAST-FORWARD to UT=2100:
  Jeb reclaims from Hanley. Hanley unused → DELETE.
  Bill reclaims from Derrick. Derrick unused → DELETE.
  Active: [Jeb, Bill, Val, Bob] = 4 ✓
```

**Stand-in dies within a temporary chain:**

```
COMMIT Recording A: Jeb, UT=1000→2000 (recovered).
  Jeb reserved UT=0→2000. Generate Hanley (P).
  Active: [Hanley, Bill, Val, Bob] = 4 ✓

COMMIT Recording B: Hanley, UT=500→700 (DEAD).
  Hanley reserved UT=0→INDEFINITE. But Jeb's slot still needs filling.
  Chain continues: generate Kirrim (P).
  Chain: Jeb → [Hanley (dead), Kirrim]
  Active: [Kirrim, Bill, Val, Bob] = 4 ✓

FAST-FORWARD to UT=2100:
  Jeb free at UT=2000. Reclaims slot.
  Kirrim unused → DELETE.
  Hanley dead + used in recording B → RETIRED (dead).
  Active: [Jeb, Bill, Val, Bob] = 4 ✓
  Retired: [Hanley (dead)]
```

The slot owner (Jeb) is temporary, so the chain continues even though the stand-in (Hanley) died. This contrasts with permanent loss of a slot owner:

**Permanent loss — slot owner dies:**

```
COMMIT Recording A: Jeb, UT=1000→1200 (DEAD).
  Jeb reserved UT=0→INDEFINITE. Permanent.
  No auto-replacement. No chain. Roster shrinks.
  Active: [_, Bill, Val, Bob] = 3.

Player hires Wehrner (P) at KSC. Spending action: -25000 funds.
  Wehrner is independent — not in any chain.
  Active: [Wehrner, Bill, Val, Bob] = 4 ✓
```

### 9.12 Dismissal rules

In vanilla KSP, the Astronaut Complex allows the player to dismiss (fire) kerbals from the roster. With Parsek's reservation and chain system, dismissal interacts with several kerbal categories differently.

**Parsek prevents dismissal of any kerbal it manages.** Reserved kerbals, active stand-ins, and retired kerbals are all protected from dismissal. The player can only dismiss kerbals that are completely outside Parsek's tracking — regular unassigned crew with no recordings and no chain involvement.

Rationale by category:

- **Reserved kerbals**: their reservation is a system invariant. Dismissing them would break recording reference integrity.
- **Active stand-ins**: they exist to maintain roster stability. Dismissing one would leave a slot empty, defeating the purpose of the replacement system.
- **Retired kerbals**: they exist in the roster for recording reference integrity. Ghosts are raw GameObjects rendered from sidecar data and don't need live roster entries for playback, but the roster entry preserves the kerbal's identity in KSP's crew system. Blocking dismissal is the safe default for v1.

### 9.13 Astronaut Complex capacity

The Astronaut Complex building level caps how many kerbals the player can hire (Level 1: 5, Level 2: 12, Level 3: unlimited). This cap only restricts player-initiated hiring. In vanilla KSP, rescued kerbals bypass the cap.

Parsek's system-generated kerbals (stand-ins and retired) also bypass the Astronaut Complex cap. They are not player hires — they are Parsek's internal roster management. The cap only governs what the player can do at the hiring screen.

This means the total number of kerbals in KSP's roster can exceed the Astronaut Complex cap due to stand-ins and retired kerbals. This is consistent with vanilla behavior (rescued kerbals already exceed the cap) and should not cause KSP issues.

### 9.14 Open questions

- **Rescue mechanics:** How does the ledger associate a rescue recording with a stranded kerbal? The rescue recording would need to reference the stranded kerbal by name and provide the recovery UT. This likely requires the recording system to detect when a vessel docks with or picks up a stranded kerbal.
- **KSP's MIA respawn:** KSP can respawn MIA kerbals after a configurable delay. Should Parsek model this, or treat MIA as permanently gone and let KSP handle the respawn outside the ledger?
- **Stand-in naming:** Should Parsek use KSP's procedural name generator, or maintain its own? KSP's generator avoids name collisions with existing roster members. RESOLVED: stand-in names are persisted in KERBAL_SLOTS ConfigNode and reused across recalculations.
- **Sandbox mode:** The reservation system applies (prevents duplicate kerbals), but replacement is trivial (free, unlimited). Does sandbox need the chain system at all, or just the reservation check? RESOLVED: sandbox uses the full chain system (prevents duplicate kerbals regardless of game mode).
- **Retired kerbal cleanup:** Should there be a mechanism for the player to acknowledge and permanently remove retired kerbals from the roster (beyond blocking dismissal)? The retired pool could grow over a long career.

### 9.15 Implementation Architecture (Post-Build)

**The kerbals module does NOT participate in the unified recalculation walk.** Unlike all other modules, `KerbalsModule` is a separate static class with its own `Recalculate()` method that operates directly on `RecordingStore.CommittedRecordings` (vessel snapshots), not on `GameAction` entries from the ledger.

**Why:** Kerbal reservation depends on crew presence in vessel snapshots (which crew members are on which vessel), not on discrete game actions. The design doc's `KerbalAssignment`, `KerbalRescue`, and `KerbalStandIn` action types exist in the `GameActionType` enum and have full serialization, but are NOT currently produced or consumed by any code path. `KerbalHire` IS produced by the `GameStateEventConverter` and consumed by the `FundsModule` (for hiring cost deduction).

**Chain and reservation system:** Operates as designed (UT=0 reservation, replacement chains, retired pool, dismissal protection) but through its own `RecalculateAndApply()` method called separately from the ledger pipeline.

**Future direction:** If kerbal data needs to participate in the unified walk (e.g., for kerbal-specific UI in the ledger actions list), the existing action types are ready. The migration path: generate `KerbalAssignment` actions at commit time from vessel snapshot crew, register `KerbalsModule` as an `IResourceModule`, and process kerbal actions in the walk.

---

## 10. Facilities Module

**Status:** Designed — spending schemas, visual state management, and destruction/repair lifecycle specified.

**Game mode applicability:** Not applicable in sandbox (all facilities max level, indestructible). Not applicable in Science mode (facilities are not upgradeable, and destruction/repair has no funds cost). Active in Career mode only.

KSC facilities are buildings with discrete levels that gate player capabilities. The facility module tracks upgrade costs, destruction events, and repair costs for funds accounting, and manages building visual state during fast-forward replay.

### 10.1 KSP's facility system

KSP has approximately 9 facilities, each with 3 levels (1 = basic, 2 = upgraded, 3 = fully upgraded). Key facilities and what they gate:

- **Launchpad / Runway**: vessel size and mass limits.
- **VAB / SPH**: part count limits.
- **Tracking Station**: tracking range, patched conics detail.
- **Astronaut Complex**: roster capacity, EVA capability.
- **Mission Control**: active contract slot limits.
- **R&D**: tech node access, science experiment availability.
- **Administration**: strategy slot limits.

Upgrading costs funds. Buildings can also be destroyed (player crashes into KSC) and repaired (costs funds).

### 10.2 Facility action schemas

```
FacilityUpgrade (spending action)
  ut:           double — frozen KSC time
  sequence:     int — order within that UT
  facilityId:   string — "LaunchPad" / "VehicleAssemblyBuilding" / etc.
  toLevel:      int — target level (2 or 3)
  cost:         float — funds spent

FacilityDestruction (recording-associated action)
  ut:           double — when the building was destroyed during flight
  recordingId:  string — the recording that caused the destruction
  facilityId:   string

FacilityRepair (spending action)
  ut:           double — frozen KSC time (whenever the player repairs)
  sequence:     int — order within that UT
  facilityId:   string
  cost:         float — funds spent
```

Upgrades and repairs are KSC spending actions (frozen UT, sequenced). Destruction is recording-associated — it happened during a specific flight. If that recording is removed by a KSP load, the destruction and any associated repair cost are pruned from the timeline.

### 10.3 Funds accounting

All three action types participate in the unified funds walk:

- **Upgrade**: deducts funds at the upgrade UT.
- **Repair**: deducts funds at the repair UT.
- **Destruction**: no direct funds cost (the destruction itself is free — the cost comes from the subsequent repair).

### 10.4 Visual state management

Building visual state updates **in real time during warp**, following the same pattern as ghost vessel playback (see section 3.3). As the warp timeline crosses facility events, Parsek sets building visuals immediately — the player sees buildings upgrade, get destroyed, and get repaired at the correct UT. This is purely visual; KSP's actual facility state is patched on warp exit.

```
For each facility, walk events in UT order:
  UPGRADE:     set visual to new level
  DESTRUCTION: set visual to destroyed
  REPAIR:      set visual to pre-destruction level
```

Between a destruction and its repair, the building is visually destroyed. This window can be any duration — the player repairs whenever they choose.

```
UT=500:  Upgrade launchpad to level 2       visual: level 2
UT=1000: Destruction (recording A crash)    visual: destroyed
UT=1000 to UT=1100: launchpad visually destroyed
UT=1100: Repair                             visual: level 2
UT=2000: Upgrade to level 3                 visual: level 3
```

Ghost vessels whose recordings include launches during the destroyed window still replay normally — ghosts are rendered from sidecar trajectory data and don't interact with KSP's facility system.

On warp exit, Parsek patches KSP's actual facility state to match the derived state at the current UT (see section 3.2).

### 10.5 Derived facility level

The facility level at any UT is derivable by walking the action history: start at default level (1), apply upgrades, destructions, and repairs in order. During warp, this drives the visual state. On warp exit, the full recalculation produces the final derived level, which is patched into KSP's state.

### 10.6 Open questions

- **Destruction detection:** How does Parsek detect building destruction during a recording? KSP fires events when buildings are hit, but the exact API for tracking destruction needs investigation. RESOLVED: `DestructibleBuilding.IsDestroyed` polling and `GameStateRecorder` event capture are implemented.
- **Partial destruction:** Can individual buildings be destroyed independently, or does KSP group them? The schema assumes per-facility tracking.
- **CustomBarnKit interaction:** CustomBarnKit modifies facility costs and progression tiers. Does the ledger need to account for non-standard level counts or costs, or does it just record what KSP reports?

---

## 11. Strategies Module

**Status:** Designed — time-windowed transforms, reservation system, and interaction with contract rewards specified.

**Game mode applicability:** Not applicable in sandbox or Science mode (no Administration building). Active in Career mode only. Strategies are lightly used by most players but affect resource balances when active, so Parsek must track them for correctness.

### 11.1 KSP's strategy system

Strategies are policies set in the Administration building that divert a percentage of one resource earned from contracts into another resource. There are eight stock strategies, all following the same pattern:

- **Source resource**: the resource being diverted from (funds, science, or reputation).
- **Target resource**: the resource being diverted to.
- **Commitment slider**: 1% to 25%, controls the diversion percentage.
- **Setup cost**: a one-time cost in the source resource, paid at activation.

Strategies only transform **contract rewards** — not milestone bonuses, not experiment science, not vessel recovery funds. Milestones pass through unaffected.

**Constraints:**
- Limited active strategy slots (Administration building level: 1 at level 1, 2 at level 2, 3 at level 3).
- Conflicting strategies (same source resource) cannot be active simultaneously. KSP enforces this natively.
- Strategies are activated and deactivated at KSC (frozen UT).

### 11.2 Strategy action schemas

```
StrategyActivate (KSC action)
  ut:             double — frozen KSC time
  sequence:       int — order within same UT
  strategyId:     string — e.g. "UnpaidResearch" / "PatentsLicensing"
  sourceResource: enum — FUNDS | SCIENCE | REPUTATION
  targetResource: enum — FUNDS | SCIENCE | REPUTATION
  commitment:     float — 0.01 to 0.25 (slider percentage)
  setupCost:      float — one-time cost in source resource (IMMUTABLE)

StrategyDeactivate (KSC action)
  ut:             double — frozen KSC time
  sequence:       int
  strategyId:     string
```

The setup cost is deducted from funds by the `FundsModule` when processing `StrategyActivate` actions. **Note (v0.3): KSP stock strategies always cost funds for setup. Reputation-based setup costs are not implemented.**

### 11.3 Strategy reservation — UT=0 to deactivation

Strategies follow the same UT=0 reservation pattern as kerbals and contracts. Once activated anywhere on the timeline, a strategy consumes its Administration building slot from UT=0 until deactivated. If never deactivated, the slot is consumed indefinitely.

**Why UT=0 reservation (not windowed):** A strategy transforms contract rewards within its active window. These transforms change the effective resource earnings — diverting funds to reputation, science to funds, etc. If a player could activate a new strategy before an existing one, the new strategy would reduce effective earnings in a different resource, potentially making existing committed spendings downstream unaffordable. Detecting this would require a trial recalculation on every strategy activation attempt. The UT=0 reservation prevents this entirely by blocking new strategies while existing ones are on the timeline.

```
StrategyReservation (derived, per strategy)
  strategyId:     string
  reservedFrom:   0 (always — invariant)
  reservedUntil:  deactivation UT, or INDEFINITE if never deactivated
```

**Slot availability:**

```
reservedSlots = count of strategies on the timeline that are active at or after current UT
                (activated and not yet deactivated, considering UT=0 reservation)
availableSlots(ut) = maxSlots (from Admin building level) - reservedSlots
```

### 11.4 Transforms during recalculation

During the recalculation walk, when a contract reward is processed, the walk checks which strategies are active at that UT. If a matching strategy is active (its source resource matches one of the reward types), the reward is transformed:

```
For each contract reward at this UT:
  For each active strategy at this UT:
    if strategy.sourceResource matches a reward type on this contract:
      diverted = rewardAmount × strategy.commitment
      rewardAmount -= diverted
      targetRewardAmount += diverted × conversionRate
```

**Note (v0.3):** `conversionRate` is hardcoded to 1.0 (deferred item D2). Actual KSP strategy conversion rates need to be extracted from strategy definitions.

The transform happens between the contracts first-tier resolution (which sets the effective flag) and the second-tier crediting (which adds effective values to funds/rep/science running balances). Only effective contract completions are transformed — non-effective duplicates produce zero rewards and therefore zero transforms.

The conversion rate (how much target resource you get per unit of diverted source resource) is defined by KSP's strategy configuration. The coding agent should extract these rates from KSP's strategy definitions.

**Implementation mechanism:** Strategy transforms write to `TransformedFundsReward`, `TransformedScienceReward`, and `TransformedRepReward` derived fields on the `GameAction` object — NOT the persisted fields. The `RecalculationEngine` resets these to the original values before each walk. Second-tier modules (`FundsModule`, `ReputationModule`) read the Transformed fields when processing `ContractComplete` actions.

### 11.5 What strategies do NOT affect

Strategies only transform contract rewards. The following are unaffected:

- **Milestone fund and reputation awards** — pass through at full value.
- **Science from experiments** — `scienceAwarded` is unaffected by strategies.
- **Vessel recovery funds** — full recovery value credited.
- **Contract advance payments** — paid at accept, not transformed.
- **Penalties** — contract failure/cancel penalties are not transformed.

### 11.6 Verified scenarios

**Basic transform within active window:**

```
Strategy "Unpaid Research" activated at UT=300 (10% REP→SCIENCE, setup cost: -5 rep).

UT=100: Milestone +10k funds, +15 rep.         (no transform — milestone)
UT=300: Strategy activates. Setup: -5 rep.
UT=400: Contract reward +40k funds, +50 rep.    → transformed: +45 rep, +5 science.
UT=600: Contract reward +30k funds, +30 rep.    → transformed: +27 rep, +3 science.
UT=700: Strategy deactivated.
UT=800: Contract reward +20k funds, +40 rep.    (no transform — outside window)
```

**Strategy window and retroactive commit:**

```
Strategy active UT=200 to UT=600.
Contract "Orbit Mun" completed at UT=300 (inside window). +50 rep → 45 rep + 5 science.

Retroactive commit: rec_B completes same contract at UT=100 (BEFORE window).
  UT=100: effective=true. +50 rep (full — outside window). +0 science.
  UT=300: effective=false (duplicate). +0 rep, +0 science.

Result: credit shifted to UT=100. No strategy diversion applied.
Rep goes UP (lost the 5 rep that was diverted). Science goes DOWN (lost the 5 science bonus).
Total resource value redistributed — not a paradox, just a different outcome.
```

**Reservation blocks new strategy on rewind:**

```
Admin building: 1 slot.
Strategy A activated at UT=1000. Reserved from UT=0.

Player rewinds to UT=500. Wants to activate Strategy B.
  reservedSlots = 1 (A). availableSlots = 1 - 1 = 0. BLOCKED.

Player must fast-forward past A's deactivation to free the slot.
Or accept that strategy A's window is part of the committed timeline.
```

### 11.7 KSP state patching

On warp exit / rewind, Parsek patches KSP's `StrategySystem` to match the ledger's strategy state at the current UT:

- Active strategies are patched into KSP with their commitment level and activation state.
- Deactivated strategies are patched as inactive.
- Future strategies (activated after the current UT) are patched as inactive at the current moment but their reservation holds the slot.

Conflict checking (same source resource) is KSP-native. Parsek ensures the correct strategies are active; KSP prevents conflicts in its own UI.

### 11.8 Open questions

- **Conversion rates:** The exact conversion rate per strategy (how much target resource per unit of diverted source) needs to be extracted from KSP's strategy configuration files or decompiled code.
- **Strategy mods:** Popular mods like Strategia completely replace the stock strategy system. Should Parsek support modded strategies, or only stock? For v1, stock only is sufficient.
- **Player usage patterns:** Strategies are lightly used by most players. The module is designed and specified, but implementation priority may be lower than other modules.

---

## 12. Example: Full Career Mun Landing Timeline

A complete Mun landing and return mission in Career mode, showing every game action the ledger would track across all resource modules.

```
UT=0 (KSC, frozen time)
  CONTRACT    Accept "Explore the Mun"                      +0 funds advance
  CONTRACT    Accept "Collect science from Mun surface"     +2,000 funds advance
  FUNDS       Vessel build "Mun Lander I"                   -18,000 funds
  KERBAL      Assign Jeb to command seat
  KERBAL      Assign Bill to crew cabin

UT=100 (launch)
  MILESTONE   First launch (if first ever)                  +2,000 funds, +5 rep

UT=300 (Kerbin orbit)
  SCIENCE     crewReport@KerbinInSpaceLow                   earned 5
  MILESTONE   First orbit of Kerbin (if first)              +5,000 funds, +10 rep

UT=2000 (Mun SOI)
  SCIENCE     evaReport@MunInSpaceHigh                      earned 8
  MILESTONE   First flyby of the Mun                        +5,000 funds, +10 rep

UT=5000 (Mun orbit)
  SCIENCE     crewReport@MunInSpaceLow                      earned 8
  MILESTONE   First orbit of the Mun                        +7,000 funds, +12 rep

UT=8000 (Mun surface, Midlands)
  SCIENCE     surfaceSample@MunSrfLandedMidlands            earned 30
  SCIENCE     crewReport@MunSrfLandedMidlands               earned 10
  SCIENCE     evaReport@MunSrfLandedMidlands                earned 8
  MILESTONE   First landing on the Mun                      +10,000 funds, +15 rep
  MILESTONE   First EVA on the Mun                          +5,000 funds, +8 rep
  CONTRACT    "Explore the Mun" completed                   +40,000 funds, +15 rep
  CONTRACT    "Collect Mun surface science" completed       +12,000 funds, +8 rep

UT=15000 (Kerbin recovery)
  SCIENCE     (onboard experiments returned at full value)
  FUNDS       Vessel recovery (% of cost by distance)       +12,000 funds
  MILESTONE   First Mun return (if tracked)                 +8,000 funds, +10 rep
  KERBAL      Jeb gains XP (Mun landing + return)
  KERBAL      Bill gains XP (Mun landing + return)

UT=15000 (KSC, frozen time — post-recovery)
  SCIENCE     Tech node "landing" seq=0                     -45 science
  SCIENCE     Tech node "survivability" seq=1               -45 science
  FACILITY    Upgrade tracking station lvl 2                -150,000 funds
  KERBAL      Hire Valentina                                -25,000 funds
```

This recording is committed as a single unit. If the player then rewinds to UT=0 and flies a different mission, every action above participates in the recalculation walk — science subject caps, milestone once-ever flags, fund balance checks, kerbal state — all reconciled against whatever the new recording contributes.

---

## 13. Gameplay Simulation Findings (v0.3)

Edge cases and gaps discovered by simulating concrete KSP gameplay scenarios against the implementation.

### 13.1 Critical Gaps (Functional — Must Fix)

**Vessel build cost not captured.** The `GameStateEventConverter` has no path for vessel launch costs. `FundsChanged` events are skipped (they're aggregate deltas, not discrete actions). The design doc (5.10) says vessel cost should produce a `FundsSpending` with `source=VESSEL_BUILD`, but no converter or commit-time code creates this action. Fix: inject vessel build cost directly at commit time from `Recording.PreLaunchFunds` delta, or add a dedicated `VesselLaunched` event type to `GameStateRecorder`.

**Vessel recovery funds not captured.** Same issue — no converter path for recovery funds to `FundsEarning` with `source=RECOVERY`. The `OnVesselRecoveryProcessing` callback exists in KSP but is not subscribed by `GameStateRecorder` for ledger purposes. Fix: subscribe to recovery event, capture recovery value, convert to `FundsEarning` at commit time.

**Science and reputation wiped on mid-career Parsek install.** When Parsek is installed on an existing career save, the ledger starts empty (no science or reputation actions). After recalculation, `ScienceModule.GetAvailableScience()` returns 0 and `ReputationModule.GetRunningRep()` returns 0. `KspStatePatcher` then patches KSP to these values, wiping the player's existing science and reputation. Funds are safe (seeded from current balance). Fix: add science and reputation seeding similar to `FundsInitial` — create `ScienceInitial` and `ReputationInitial` action types that capture the current balance when the ledger is first created.

**Contract science rewards not credited.** `ContractComplete` actions have a `ScienceReward` field, but `ScienceModule` only processes `ScienceEarning` and `ScienceSpending` — it ignores `ContractComplete`. Contract science rewards are captured but never added to the science balance. Fix: add `ContractComplete` handling in `ScienceModule.ProcessAction` that reads `TransformedScienceReward` when `Effective=true`.

### 13.2 Edge Cases Discovered

**Facility destroyed state not patched.** `FacilitiesModule` correctly tracks the `Destroyed` flag per facility, but `KspStatePatcher.PatchFacilities` only patches facility levels via `SetLevel` — it does not set buildings as destroyed or repaired. After rewind past a crash, KSP buildings would show incorrect visual state.

**Sequence numbers not assigned during event conversion.** `GameStateEventConverter` creates actions with `Sequence=0` for all KSC events at the same UT. If a player unlocks two tech nodes at the same frozen KSC time, their relative order is undefined. Benign for independent spendings but could matter if one spending's affordability depends on the order.

**ContractAccept sorts with spendings, not earnings.** `ContractAccept` is not in `IsEarningType` (it's neither earning nor spending in the classifier). At the same UT, the advance payment processes after explicit earnings. Functionally harmless (the advance still adds to balance before spendings), but semantically inconsistent with "credits before debits."

**Multiple strategy transforms stack multiplicatively.** If two strategies divert from different source resources on the same contract completion, each reads the previously-transformed value. Strategy A diverts 10% of funds, then Strategy B diverts 20% of the remaining funds = 28% total, not 30%. This may differ from KSP's actual multi-strategy behavior. Needs in-game verification.

**Recording-associated spending survives reconciliation when recording is pruned.** If recording A has a vessel build cost (`FundsSpending` with `RecordingId=A`), and recording A is removed via KSP load, reconciliation prunes earnings by recording ID but spending pruning is by UT only. The spending persists even though its associated recording is gone. Acceptable since KSP load is authoritative and the loaded save's state accounts for the spending.

**Empty ledger + mid-career = no replay of past events.** When Parsek is installed mid-career, only the funds seed is captured. All prior tech unlocks, facility upgrades, contract completions, and kerbal assignments are NOT in the ledger. The recalculation walk only knows about events captured by Parsek from this point forward. This is by design (the ledger starts fresh) but means the game actions list will be incomplete for the pre-Parsek period.

### 13.3 Performance Notes

The recalculation walk is O(n log n) sort + O(n × m) dispatch (n=actions, m=7 modules). For 500 actions, this completes in well under 1ms. The most expensive per-action operation is the reputation curve evaluation (integer-step loop per nominal rep value), but even with 50 rep actions this is trivially fast. LINQ `SortActions` allocates a new list each call — minor GC pressure, could be optimized to sort in-place if needed.

No batching for rapid commits: 10 quick commits = 10 full recalculations. Acceptable for typical gameplay (commits are rare events, not per-frame).

---

## Appendix A: Design Principles

### A.1 Timeline model — the Git analogy

Parsek's timeline operates on a model analogous to Git version control:

**Commit** is permanent. Once a recording is committed to the timeline, it becomes part of the history. Its immutable data (science values, fund amounts, kerbal assignments) is fixed. The timeline is **additive** — recordings can be added but never deleted through Parsek.

**Rewind is a soft operation — like `git checkout` or `git revert`.** The player jumps to an earlier point on the timeline. All committed recordings survive. No data is lost. The player can fly a new mission from that point and commit it, creating a new branch of activity on the same timeline. The recalculation walk adapts — derived values are recomputed to account for the new recording's presence alongside existing ones.

**KSP load is a hard reset — like `git reset --hard`.** Loading a save discards everything that isn't in that save's state. Recordings not referenced in the loaded save are gone from the timeline. The ledger is pruned to match. This is the only destructive operation, and it's KSP's native behavior — Parsek accepts it rather than fighting it.

**Loading without saving loses everything since the last save.** There is no undo for a KSP load. If the player commits recordings and then loads an earlier save without saving first, those recordings are gone. This is the same as losing uncommitted work after a `git reset --hard`.

### A.2 No time-travel paradoxes

The system is designed so that paradoxes are structurally impossible, not detected after the fact. Every constraint works by prevention:

- **Science subject caps** prevent overcredit. Multiple recordings collecting from the same subject are reconciled by the hard-cap walk. Chronologically first recording gets priority.
- **Spending reservation** prevents overspending. All committed spendings (past and future) are reserved against available earnings. The player can't add new spendings that would create a deficit. Applies to science (tech nodes) and funds (vessels, facilities, hires).
- **Kerbal UT=0 reservation** prevents duplicate kerbals. A kerbal used in a committed recording is locked from UT=0 as a continuous block, making it impossible to assign them to an overlapping mission.
- **Contract UT=0 reservation** prevents slot overflow. Accepted contracts consume Mission Control slots from UT=0, preventing the player from over-accepting after rewind.
- **Strategy UT=0 reservation** prevents resource diversion conflicts. Active strategies hold Administration building slots from UT=0, blocking new strategies that could reduce effective earnings and break downstream spendings.
- **Once-ever flags** prevent duplicate credit. Milestones and contract completions are credited to the chronologically first recording only. Later duplicates are zeroed out.
- **Seeded balance** prevents false negatives. The funds walk starts from the career starting balance extracted from the save, not from zero.

The player never sees a broken state that needs manual resolution. If something goes wrong (bugs, corruption), KSP load is the escape hatch.

### A.3 Additive timeline, adaptive on add

The recording system is additive — new recordings join the timeline alongside existing ones. The timeline adapts when a recording is added:

- Derived values are recomputed from scratch (the recalculation walk from UT=0).
- Earlier recordings get chronological priority (first in time = full credit).
- Later recordings get the remainder (reduced effective values if caps apply).
- Future events are locked on the timeline — their immutable values don't change — but their derived values are recalculated on every trigger.

Deletion is not available through Parsek. The player cannot remove individual recordings. They can:

- **Rewind** — non-destructive, all recordings survive, just jump to an earlier point.
- **KSP load** — destructive, resets everything to the loaded save's state. Effectively deletes all timeline state from the loaded point forward, including any recordings and spendings not in that save.

### A.4 Immutable recordings, derived state

Raw values from KSP (`scienceAwarded`, fund amounts, milestone triggers, kerbal end states) are written once at recording time and never changed. They are stored in the ledger file as immutable facts.

All computed values (`effectiveScience`, running balances, affordability flags, kerbal reservation status, facility visual state) are derived from a full recalculation pass. Nothing is cached between recalculations. The ledger file is the sole input; derived state is the sole output.

### A.5 Modular and isolated subsystems

Each resource module is independent:

- Has its own earning/spending rules, caps, and constraints.
- Owns its portion of the recalculation walk.
- Does not call other modules or depend on their internal state.
- Shares only the sorted action stream and the `recordingId` key with other modules.

Modules are isolated in logic but connected by **data flow dependency**. First-tier modules (Science, Milestones, Contracts) compute effective values on actions. Second-tier modules (Funds, Reputation) read those effective values during the same walk. This is one-directional data flow, not bidirectional communication — first-tier modules never read from second-tier modules.

This isolation means modules can be designed, tested, and debugged independently. Adding a new module (e.g. a future "ore" or "electric charge" module) requires no changes to existing modules — only a new handler in the walk dispatcher.

The coupling between the recording system and the game actions system is exactly one field: `recordingId`. The recording system doesn't know about science, funds, or kerbals. The ledger doesn't know about trajectories, DAGs, or ghosts. They share a key and nothing else.

### A.6 Conservative over precise

Where Parsek's model diverges from KSP's exact mechanics (e.g. hard cap vs diminishing returns curve), it errs on the side that prevents overcredit rather than the side that maximizes accuracy. A slightly generous credit that stays under the cap is acceptable. A credit that exceeds the cap is not.

### A.7 Commitment is the deliberate choice

Recordings only affect the timeline when the player explicitly commits. The player can always revert to launch and discard a recording. The harshness of permanent consequences (kerbal death, resource commitment) is mitigated by the player's control over what gets committed. Parsek never forces a recording onto the timeline.
