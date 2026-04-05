# Parsek — Timeline System Design

*Comprehensive design specification for Parsek's unified timeline view — a chronological, read-only query layer across recordings, game actions, and milestones that gives the player a single place to see everything that has happened and will happen on the committed timeline.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies how the timeline aggregates and presents data from the flight recorder system (see `parsek-flight-recorder-design.md`) and the game actions system (see `parsek-game-actions-and-resources-recorder-design.md`).*

**Version:** 0.1 (Design)
**Status:** Design phase. Depends on v0.6 (Game Actions System) being complete.
**Out of scope:** Recording, playback, ghost visuals, vessel spawning. See `parsek-flight-recorder-design.md`. Resource tracking, ledger, recalculation engine. See `parsek-game-actions-and-resources-recorder-design.md`.

---

## 1. Introduction

Parsek has two existing views of committed data:

- The **Recordings Manager** is vessel-centric: a list of recordings with per-recording controls (enable/disable, rewind, loop, group). It answers "what vessels did I record?"
- The **Game Actions window** is resource-centric: a flat list of ledger actions and legacy game state events with sorting and deletion. It answers "what resource transactions happened?"

Neither answers the question the player actually has: **what happened, and what will happen, in chronological order across all systems?**

The timeline is a unified, chronological view that pulls from all of Parsek's data systems — recordings, game actions, part events, segment events — normalizes them into a common entry shape, and presents them sorted by universal time (UT). It replaces the Game Actions window entirely.

### 1.1 What the timeline is

The timeline is a **read-only query layer**. It does not own data. Recordings remain in `RecordingStore`. Game actions remain in the `Ledger`. Part events remain inside their parent `Recording`. The timeline reads from all of them, constructs a flat list of entries, and presents it.

This is analogous to a database view: it computes a result set from underlying tables. If a recording is committed or rewound, the timeline rebuilds. If a ledger action is added (facility upgrade, tech unlock), the timeline rebuilds. The timeline never modifies the data it reads.

### 1.2 What the player sees

The player sees the full committed history at all times. The timeline has a **current-UT marker** that divides the list into two visual halves:

- **Above the marker** (past): events that have already played out. Vessels spawned, resources applied, milestones credited. Full color.
- **Below the marker** (future): events that will play out when the player advances time. Ghost vessels will appear, resources will change, milestones will trigger. Dimmed but not hidden.

There are no branches and no hidden future — the player recorded that future and committed it. The timeline shows the consequences of their choices.

| Situation | What the player sees in the timeline |
|-----------|--------------------------------------|
| Three recordings committed | All three recordings' events interleaved chronologically — launches, staging, milestones, science, contracts, all sorted by UT |
| Rewind to before a recording | That recording's events appear below the current-UT marker (future), dimmed. They will play out again as the player advances. |
| Active flight in progress | Uncommitted events from the current flight appear at the bottom in a distinct "Uncommitted" section. They join the main timeline on commit. |
| Facility upgrade at KSC | The upgrade action appears instantly in the timeline at the current UT. |
| Contract completed during a recording | The completion action appears at the UT it occurred, alongside the recording's part events at similar UTs. |

### 1.3 How the timeline differs from the Recordings Manager

The Recordings Manager and the timeline are **complementary views of the same data**, cross-linked but not merged:

| Aspect | Recordings Manager | Timeline |
|--------|-------------------|----------|
| Organized by | Vessel (one row per recording) | Time (one row per event) |
| Shows | Recording metadata, playback controls, chain/loop config | All events from all systems, interleaved chronologically |
| Controls | Enable/disable, rewind, loop, group, hide | Rewind (same mechanism), filter, expand/collapse |
| Answers | "What recordings do I have?" | "What happened at UT 5000?" |
| Cross-link | Click recording → timeline scrolls to it | Click timeline recording header → Recordings Manager selects it |

Both windows can be open simultaneously.

### 1.4 Three data sources

The timeline draws from three independent systems. Each system owns its data; the timeline only reads.

**1. Recordings** (`RecordingStore.CommittedRecordings`)

Each committed recording contributes:
- A **start event** at `rec.StartUT` (vessel name, launch)
- An **end event** at `rec.EndUT` (terminal state: recovering, destroyed, orbiting, stranded)
- A **spawn event** at `rec.StartUT` if the recording produces a ghost vessel
- **Part events** from `rec.PartEvents` — engine ignitions, staging, parachute deployments, docking, etc.
- **Segment events** from `rec.SegmentEvents` — controller changes, crew transfers, part additions/removals
- **Flag events** from `rec.FlagEvents` — flag plantings
- **Ghost chain windows** — computed from chain members' UT ranges, showing when a vessel is ghosted and untouchable

**2. Game actions** (`Ledger.Actions`)

Each ledger action is a single timeline entry: science earned, funds spent, milestone achieved, contract completed, kerbal hired, facility upgraded, strategy activated. The ledger is the canonical source — the timeline uses `GameActionDisplay` for descriptions and colors, and reads derived fields (`Effective`, `Affordable`, `EffectiveScience`, etc.) set by the recalculation engine.

**3. Legacy events** (`MilestoneStore.Milestones`, `GameStateStore.Events`)

Saves started before the ledger system have committed events stored as `GameStateEvent` entries in `MilestoneStore`. The timeline displays these in their existing format using `GameStateEventDisplay`. Uncommitted events from the current flight session (in `GameStateStore`) appear in a separate section at the bottom.

As saves migrate to the ledger system (legacy events are converted to `GameAction` entries on commit via `GameStateEventConverter`), the legacy collector produces fewer entries. Eventually, for saves fully migrated, it produces none.

---

## 2. Design Philosophy

### 2.1 Read-only, not read-write

The timeline displays data. It never modifies it. The one action it offers — rewind — is an operation on the game state (loading a quicksave), not on the timeline's data.

Why: the timeline pulls from three independent systems with their own persistence, consistency rules, and mutation APIs. Allowing edits through the timeline would require the timeline to own or mutate data it doesn't own, creating synchronization complexity. Edits to committed data happen through the systems that own that data: Recordings Manager for recordings, direct KSP interactions for game actions.

### 2.2 Time-centric, not vessel-centric or resource-centric

The Recordings Manager groups by vessel. The Game Actions window groups by resource type. The timeline groups by **time**. This reveals causal chains that neither other view shows:

> UT 600: Milestone "First orbit (Kerbin)" → +5000 funds  
> UT 800: Facility upgrade "Runway → Lv.2" → -18000 funds  
> UT 900: Contract complete "Orbit Kerbin" → +8000 funds  

The player sees that the milestone funded the runway upgrade. This causal chain is invisible in the Recordings Manager (which shows the recording as a single block) and invisible in the Game Actions window (which sorts by type, not time, by default).

### 2.3 Full visibility, no hidden future

Both past and future events are always visible. Future events are visually dimmed but never hidden. This is consistent with Parsek's core principle: the player recorded that future and committed it. They should see the consequences of their commitments.

### 2.4 Significance filtering, not data filtering

The timeline can contain hundreds of events for an active career. Rather than hiding events, the timeline classifies them into **significance tiers** (T1/T2/T3) and defaults to showing only the high-level skeleton (T1). The player expands to see more detail. This keeps the default view clean without hiding information.

### 2.5 The timeline replaces the Game Actions window

The timeline subsumes everything the Game Actions window does (showing game actions sorted by time) and adds recording events, part events, and visual structure. Keeping both would create redundancy. The Game Actions button becomes the Timeline button. The resource budget summary, epoch display, and retired kerbals section move into the timeline window.

---

## 3. Architecture

### 3.1 Coupling to other systems

The timeline has **read-only coupling** to three systems:

```
RecordingStore ──reads──→ TimelineBuilder ←──reads── Ledger
                              ↑
                          reads│
                              │
                    MilestoneStore / GameStateStore
                         (legacy)
```

The timeline never writes to any of these systems. It never calls `Ledger.AddAction()`, `RecordingStore.CommitPending()`, or `MilestoneStore.CreateMilestone()`. The only side effect it triggers is through the rewind button, which delegates to `RecordingStore.InitiateRewind()` via the existing `ShowRewindConfirmation()` dialog — the same mechanism already used by the Recordings Manager.

### 3.2 TimelineEntry — the normalized event shape

Every event from every source is normalized into a common shape:

```
TimelineEntry
    double            UT              — universal time
    TimelineEntryType Type            — discriminator enum (see §3.3)
    string            DisplayText     — one-line human-readable description
    TimelineSource    Source          — Recording, GameAction, Legacy, or Derived
    SignificanceTier  Tier            — T1, T2, or T3
    Color             DisplayColor    — green (earning), red (spending), white (neutral)
    string            RecordingId     — source recording ID; null for KSC-only actions
    string            VesselName      — vessel name if applicable; null otherwise
    bool              IsEffective     — false if zeroed by recalculation (duplicate milestone, etc.)
    PartEventType?    DetailType      — underlying PartEventType for grouped part entries; null otherwise
```

The `DetailType` field is necessary because some part event groups (engines, parachutes) span two significance tiers. An engine ignition is T2; an engine throttle change is T3. Both have `TimelineEntryType = PartEngine`, but the tier is determined by the underlying `PartEventType`. The builder inspects `DetailType` during construction to assign the correct tier.

`TimelineEntry` is a **view object** — constructed on demand, never serialized, never persisted.

### 3.3 TimelineEntryType

Discriminator enum covering every event source, organized by origin:

**Recording lifecycle** (3 types):
`RecordingStart`, `RecordingEnd`, `VesselSpawn`

**Game actions** (23 types, 1:1 with `GameActionType`):
`ScienceEarning`, `ScienceSpending`, `FundsEarning`, `FundsSpending`, `ReputationEarning`, `ReputationPenalty`, `MilestoneAchievement`, `ContractAccept`, `ContractComplete`, `ContractFail`, `ContractCancel`, `KerbalAssignment`, `KerbalHire`, `KerbalRescue`, `KerbalStandIn`, `FacilityUpgrade`, `FacilityDestruction`, `FacilityRepair`, `StrategyActivate`, `StrategyDeactivate`, `FundsInitial`, `ScienceInitial`, `ReputationInitial`

**Part events** (10 grouped types):
`PartStaging` (Decoupled, FairingJettisoned, ShroudJettisoned), `PartDestruction` (Destroyed), `PartEngine` (EngineIgnited, EngineShutdown, EngineThrottle), `PartParachute` (ParachuteDeployed, ParachuteSemiDeployed, ParachuteCut, ParachuteDestroyed), `PartDocking` (Docked, Undocked), `PartDeployable` (DeployableExtended, DeployableRetracted, GearDeployed, GearRetracted, CargoBayOpened, CargoBayClosed, LightOn, LightOff, LightBlinkEnabled, LightBlinkDisabled, LightBlinkRate), `PartRCS` (RCSActivated, RCSStopped, RCSThrottle), `PartRobotics` (RoboticMotionStarted, RoboticPositionSample, RoboticMotionStopped), `PartThermal` (ThermalAnimationHot, ThermalAnimationMedium, ThermalAnimationCold), `PartInventory` (InventoryPartPlaced, InventoryPartRemoved)

**Segment events** (9 types, 1:1 with `SegmentEventType`):
`ControllerChange`, `ControllerDisabled`, `ControllerEnabled`, `CrewLost`, `CrewTransfer`, `SegmentPartDestroyed`, `SegmentPartRemoved`, `SegmentPartAdded`, `TimeJump`

**Derived** (1 type):
`GhostChainWindow` — a duration entry showing when a vessel is ghosted

**Flag events** (1 type):
`FlagPlanted`

**Legacy** (1 type):
`LegacyEvent` — for `GameStateEvent` entries from pre-ledger `MilestoneStore` milestones

Part events are grouped by function (not 1:1 with `PartEventType`) to keep the type count manageable. There are 35 `PartEventType` values; exposing all of them would make tier tables and filter logic unwieldy. The underlying `PartEventType` is preserved in `DetailType` for tier assignment and detail display.

### 3.4 Entry collection

`TimelineBuilder.Build()` constructs the entry list from source data. It accepts data sources as parameters (not reading global state directly) for testability:

```
TimelineBuilder.Build(
    IReadOnlyList<Recording> committedRecordings,
    IReadOnlyList<GameAction> ledgerActions,
    IReadOnlyList<Milestone> milestones,      // legacy
    IReadOnlyList<GameStateEvent> uncommitted, // current flight
    uint currentEpoch,
    double currentUT
) → List<TimelineEntry>
```

Four collectors run in sequence, then results are merged:

**Recording Collector** — iterates committed recordings:
- Emits `RecordingStart` at `rec.StartUT` using vessel name
- Emits `RecordingEnd` at `rec.EndUT` using `rec.TerminalStateValue` for display text (e.g., "Recovering", "Destroyed", "Orbiting Kerbin")
- Emits `VesselSpawn` at `rec.StartUT` if `rec.PlaybackEnabled` and recording will produce a ghost
- Maps each `PartEvent` to a grouped `TimelineEntryType`, stores the original `PartEventType` in `DetailType`, constructs display text from part name and event description
- Maps each `SegmentEvent` 1:1 to `TimelineEntryType`, constructs display text from event details
- Maps each `FlagEvent` to `FlagPlanted`
- Computes ghost chain windows: for each distinct `ChainId` among committed recordings, finds the earliest `StartUT` and latest `EndUT` across all chain members (branch 0), emits a `GhostChainWindow` entry with display text "VesselName: ghost UT start–end"

**Game Action Collector** — iterates `Ledger.Actions`:
- Maps each `GameAction.Type` directly to the matching `TimelineEntryType`
- Uses `GameActionDisplay.GetDescription(action)` for display text
- Uses `GameActionDisplay.GetColor(action.Type)` for display color
- Reads `action.Effective` into `entry.IsEffective`
- Reads `action.RecordingId` into `entry.RecordingId`
- For `ScienceInitial` and `ReputationInitial` (which have no case in `GameActionDisplay.GetDescription`), `TimelineEntryDisplay` provides custom descriptions: "Starting science: {value}", "Starting reputation: {value}"

**Legacy Collector** — iterates `MilestoneStore.Milestones` for the current epoch:
- For each committed milestone matching `currentEpoch`, iterates its events
- Skips resource events and crew status changes (`IsMilestoneFilteredEvent`)
- Uses `GameStateEventDisplay.GetDisplayCategory` and `GetDisplayDescription` for display text
- Marks events as `TimelineEntryType.LegacyEvent`
- All legacy entries are T2 (they represent committed game state events — significant but not mission-structure-defining)

**Uncommitted Collector** — iterates `GameStateStore.Events` for events after the last milestone's EndUT in the current epoch:
- Applies the same filtering as the legacy collector
- Marks entries with a distinct visual treatment (see §5.9)

**Merge** — concatenate all entries from all four collectors, stable sort by UT ascending.

### 3.5 Cache invalidation

`TimelineBuilder.Build()` is not free — it iterates all committed recordings and all ledger actions. But it runs infrequently because the entry list only changes on explicit user actions:

- Recording committed → rebuild
- Rewind completed → rebuild
- KSC spending (tech unlock, facility upgrade, kerbal hire) → rebuild
- Scene change → rebuild
- Warp exit (`onTimeWarpRateChanged` when rate drops to 1) → rebuild (because spawning may have occurred)

The current-UT divider position updates on every draw call (reads `Planetarium.GetUniversalTime()`, finds insertion index via binary search in the cached sorted list — cheap). The entry list itself is only rebuilt on the triggers above.

### 3.6 TimelineSource

```
enum TimelineSource
{
    Recording,      // RecordingStore — trajectory lifecycle, part/segment/flag events
    GameAction,     // Ledger — resource transactions, contracts, milestones, kerbals, etc.
    Legacy,         // MilestoneStore — pre-ledger game state events
    Derived,        // Computed by timeline builder (ghost chain windows)
}
```

---

## 4. Significance Tiers

Every event type has a default significance tier. The tier controls which events appear in the default view and which require the player to expand or filter.

### 4.1 T1 — Always visible

High-level mission structure. A T1-only view shows the skeleton of the player's career: when missions launched and ended, what milestones were achieved, when the KSC changed.

| Entry Type | Why T1 |
|---|---|
| RecordingStart | Mission launch — fundamental timeline anchor |
| RecordingEnd | Mission conclusion — the outcome matters |
| VesselSpawn | Ghost materialization — directly observable in the world |
| MilestoneAchievement | One-time progression gate, often with large rewards |
| ContractComplete | Mission objective achieved — major career event |
| ContractFail | Mission objective failed — consequences visible |
| FacilityUpgrade | KSC progression — enables new capabilities |
| FacilityDestruction | KSC regression — disables capabilities |
| KerbalHire | Roster expansion — funds spent, new capability |
| CrewLost | Kerbal death — irreversible, narratively significant |
| GhostChainWindow | Explains why a vessel is untouchable and when it resolves |
| FundsInitial | Career start — baseline reference |
| ScienceInitial | Career start — baseline reference |
| ReputationInitial | Career start — baseline reference |

### 4.2 T2 — Visible on expand or filter

Significant vessel events and resource transactions. Visible when the player expands a recording's group or enables a type filter.

| Entry Type | Why T2 |
|---|---|
| PartStaging | Decoupling/fairing jettison — major flight events |
| PartDocking | Docking/undocking — vessel identity change |
| PartEngine (ignition/shutdown only) | Engine lifecycle — observable thrust change |
| PartParachute (deploy only) | Chute deployment — critical landing event |
| ScienceEarning | Experiment credit — progression |
| ScienceSpending | Tech unlock — progression |
| FundsEarning | Income — contract reward, recovery, milestone bonus |
| FundsSpending | Expense — vessel build, facility, hire |
| ReputationEarning | Rep gain |
| ReputationPenalty | Rep loss |
| ContractAccept | Commitment — advance payment, slot consumed |
| ContractCancel | Commitment withdrawn — penalties applied |
| KerbalAssignment | Crew mission tracking |
| KerbalRescue | Crew recovery — roster change |
| KerbalStandIn | Replacement crew — explains roster changes |
| FacilityRepair | KSC restoration |
| StrategyActivate | Policy change — reward diversion begins |
| StrategyDeactivate | Policy change — diversion ends |
| CrewTransfer | Crew movement between parts |
| FlagPlanted | Player milestone marker |
| ControllerDisabled | Loss of control — critical flight event |
| ControllerEnabled | Control restored |
| LegacyEvent | Pre-ledger committed events |

**Sub-tier routing**: Some grouped part event types span tiers. `PartEngine` and `PartParachute` have both T2 and T3 members. The builder inspects the underlying `PartEventType` (stored in `DetailType`) to assign the correct tier:

| Group | T2 members | T3 members |
|-------|-----------|-----------|
| PartEngine | EngineIgnited, EngineShutdown | EngineThrottle |
| PartParachute | ParachuteDeployed | ParachuteSemiDeployed, ParachuteCut, ParachuteDestroyed |

### 4.3 T3 — Visible on explicit request

Detailed part-level telemetry and low-signal events. Only shown when the player enables "show all" or filters to a specific event type.

| Entry Type | Why T3 |
|---|---|
| PartEngine (throttle changes) | Per-frame noise — only meaningful in aggregate |
| PartParachute (cut/destroyed/semi-deploy) | Rare, low visual significance |
| PartDestruction | Individual part loss (vessel-level is T1 via RecordingEnd) |
| PartDeployable | Solar panel/antenna/gear/light/cargo bay state changes |
| PartRCS | RCS activation/throttle — high frequency, low signal |
| PartRobotics | Robotic motion samples — continuous, low signal |
| PartThermal | Thermal animation state — derived from flight conditions |
| PartInventory | Inventory part placement/removal |
| ControllerChange | Probe core switch — rare, technical |
| SegmentPartDestroyed | Part destroyed without vessel split — technical |
| SegmentPartRemoved | Part removed (inventory) — technical |
| SegmentPartAdded | Part added (inventory) — technical |
| TimeJump | Discrete UT skip — metadata |

### 4.4 Visibility rules

- **Default view**: T1 only. Clean, high-level career overview.
- **Expanded recording group**: T1 + T2 for that recording's entries. Shows mission-level detail.
- **Filter active**: Shows entries matching the active filter regardless of tier.
- **"Show all" toggle**: T1 + T2 + T3. Full telemetry view.
- **Ineffective entries** (`IsEffective == false`): shown with strikethrough styling and gray color in any view. These are duplicate milestones, duplicate contract completions, etc. — the player sees they exist but knows they had no effect.

---

## 5. Player Interaction

This section describes what the player sees and does at the UI level. Every element here is something the player directly interacts with.

### 5.1 Opening the timeline

The main Parsek window's "Game Actions" button becomes "Timeline". The badge count shows `MilestoneStore.GetPendingEventCount() + GameStateStore.GetUncommittedEventCount()` (same as today). Clicking it opens the timeline sub-window.

### 5.2 Window layout

The timeline window has four visual zones, top to bottom:

**Zone 1: Resource Budget** — identical to the current Game Actions window's resource budget summary. Shows reserved funds, science, and reputation versus available amounts. Over-committed resources highlighted in red with warning text. In sandbox mode, this zone is hidden (no resources). In science mode, only science is shown.

**Zone 2: Filter Bar** — a row of controls:
- **Tier selector**: three cumulative buttons labeled "T1", "T1+2", "All". Clicking "T1+2" enables T1 and T2. Clicking "All" enables all three. Default is "T1".
- **Source toggles**: "Recordings" and "Actions" toggle buttons. Both on by default. Turning off "Recordings" hides recording lifecycle and part/segment events. Turning off "Actions" hides game action entries.
- **Sparkline toggle**: "Resources" button to show/hide the resource sparkline (see §7). Off by default.

**Zone 3: Entry List** — the main scrollable list of timeline entries, divided by the current-UT marker. This is where the player spends most of their time. Details in §5.3–5.8.

**Zone 4: Footer** — epoch display ("Epoch: N (N reverts)"), retired kerbals count, and Close button.

### 5.3 Entry list — past events

Events with UT ≤ current UT appear above the current-UT divider. Each entry row contains:

- **UT column** (fixed width ~90px) — formatted via `KSPUtil.PrintDateCompact(ut, true)`
- **Color indicator** — small colored label matching the entry's `DisplayColor`: green for earnings, red for spendings, white for neutral
- **Description** — the `DisplayText` string, filling remaining width

Past events are rendered in full color. This is what has already played out.

### 5.4 Current-UT divider

A horizontal separator line with "── UT {value} (now) ──" label. The divider position updates every draw call (reads `Planetarium.GetUniversalTime()`), but the entry list itself only rebuilds on cache invalidation triggers.

**During time warp**: the divider does **not** live-update. It stays at its pre-warp position. On warp exit, the cache is invalidated, the entry list rebuilds, and the divider jumps to the correct position. This avoids per-frame timeline work during warp.

### 5.5 Entry list — future events

Events with UT > current UT appear below the divider. Same layout as past events, but rendered at 50% alpha (dimmed text and color). Not hidden — the player can see what will happen.

### 5.6 Recording group headers

When a recording has entries in the timeline, its `RecordingStart` entry acts as a **collapsible group header**:

```
▶ UT 500   Mun Explorer (UT 500–3200, recovering)  [4 events]  [⟲]
```

- **Collapse arrow** (▶/▼): toggles child entries (T2/T3 part events, segment events, game actions linked to this recording via `RecordingId`). Collapsed by default.
- **UT range**: shows `StartUT–EndUT` and terminal state
- **Event count badge**: number of hidden child entries
- **Rewind button** [⟲]: only on `RecordingStart` entries. Triggers rewind to that recording's launch point.

T1 entries within a recording group (e.g., `ContractComplete` with a matching `RecordingId`) are always visible regardless of collapse state. Only T2/T3 entries collapse.

Expanded view shows child entries indented:

```
▼ UT 500   Mun Explorer (UT 500–3200, recovering)  [4 events]  [⟲]
    UT 510   Engine ignition: Mainsail
    UT 520   Decoupled: Launch clamps
    UT 600   Science: crewReport@KerbinSrfLaunchpad +5.0 sci
    UT 3100  Parachute deployed: Mk16
```

### 5.7 Rewind from the timeline

Clicking the [⟲] button on a `RecordingStart` entry invokes the same rewind flow as the Recordings Manager:

1. `ShowRewindConfirmation(rec)` displays a confirmation dialog: "Rewind to 'Vessel Name' launch at UT?"
2. The dialog shows how many future recordings will replay as ghosts
3. On confirm, `RecordingStore.InitiateRewind(rec)` executes the rewind: copies the quicksave, strips spawned vessels, preprocesses the save file, loads it via `GamePersistence.LoadGame`, transitions to Space Center

No new rewind infrastructure is needed. The timeline provides an additional surface for invoking existing rewind, complementing the Recordings Manager's rewind buttons.

### 5.8 Ineffective entries

Game action entries where the recalculation engine set `Effective = false` (duplicate milestones, duplicate contract completions, or unaffordable spendings) are rendered with:

- Strikethrough text style
- Gray color override (regardless of earning/spending color)
- "(duplicate)" or "(unaffordable)" suffix in the display text

These entries are visible at their normal tier. The player sees that a second recording also achieved "First orbit" but was zeroed because the first recording got credit.

### 5.9 Uncommitted events

During an active flight, uncommitted events from `GameStateStore` appear in a distinct section below the main timeline:

```
── Uncommitted (current flight) ──
  UT 4500  Contract: Accepted "Land on Mun"
  UT 4600  Science: crewReport@KerbinSrfLaunchpad +5.0 sci
```

These events are not yet part of the committed timeline. They will join it when the player commits the current recording. They are rendered in normal color (not dimmed) but visually separated from committed entries by a labeled divider.

Uncommitted events use the same filtering as the legacy collector: resource events (`FundsChanged`, `ScienceChanged`, `ReputationChanged`) and crew status changes are excluded (they are shown in the resource budget summary instead).

### 5.10 Epoch display

The footer shows "Epoch: N (N reverts)" when the current epoch > 0, identical to the current Game Actions window. This tells the player how many times they've rewound in this save.

### 5.11 Retired kerbals

Retired stand-in kerbals are shown in the footer as a collapsed section: "Retired Stand-ins (N)". Expanding shows the kerbal names. This replaces the dedicated section at the bottom of the current Game Actions window.

### 5.12 Cross-link with Recordings Manager

The timeline and Recordings Manager share a `selectedRecordingId`:

- **Timeline → Manager**: clicking a recording group header in the timeline sets `selectedRecordingId`. The Recordings Manager (if open) scrolls to and highlights that recording.
- **Manager → Timeline**: selecting a recording in the Recordings Manager sets `selectedRecordingId`. The timeline scrolls to that recording's first entry and highlights it.

Implementation note: IMGUI (`GUILayout`-based) scroll views do not have a built-in "scroll to element" API. The implementation must compute the Y offset of the target entry based on accumulated row heights and set `timelineScrollPos.y` manually. This is non-trivial but achievable with a row-height accumulator during the draw pass.

---

## 6. Resource Sparkline

### 6.1 What the player sees

Three thin horizontal sparkline strips at the bottom of the entry list (above the footer), showing funds, science, and reputation over time:

```
▁▁▂▃▃▅▆▆▅▃▂▂▃▅▇▇ Funds     (yellow/gold)
▁▁▁▂▂▃▃▃▃▃▃▃▃▃▃▃ Science   (cyan/blue)
▁▂▂▃▃▃▂▂▃▃▄▄▅▅▆▆ Reputation (orange)
```

Each strip spans the full UT range of the timeline. A vertical hairline marks the current UT. The sparkline is toggled on/off via the "Resources" button in the filter bar. Off by default.

In sandbox mode (no resources), the sparkline toggle is hidden. In science mode (no funds/reputation), only the science sparkline appears.

### 6.2 Data source

The recalculation engine walks all ledger actions sorted by UT, dispatching each to resource modules that maintain running balances. The sparkline data comes from querying module state after each action in the walk.

To expose this without modifying the recalculation engine's pure computation model:

- Add a `List<ResourceSnapshot> ResourceHistory` output field to `RecalculationEngine`
- `ResourceSnapshot` is a simple struct: `{ double UT; double Funds; double Science; float Reputation; }`
- After processing each action that changes a balance, the engine appends a snapshot by querying the current running values from `FundsModule`, `ScienceModule`, and `ReputationModule`
- `TimelineBuilder` reads `ResourceHistory` and passes it to the sparkline renderer

The running balances are already computed by the modules during the walk — this just makes them visible as an ordered list.

### 6.3 Rendering

The sparkline is a procedurally generated `Texture2D` rendered via `GUI.DrawTexture`. The texture is regenerated on timeline cache invalidation (same triggers as the entry list rebuild). At typical timeline lengths (dozens to hundreds of game actions), snapshot lists are small and texture generation is trivial.

Each sparkline strip is ~30 pixels tall, full window width. Snapshot UTs map linearly to X pixels. Resource values map linearly to Y pixels (auto-scaled to min/max per resource).

**Future**: hover interaction (mouse X → UT → interpolated resource value → tooltip) is not in v1 but is a natural extension.

---

## 7. Game Mode Considerations

### 7.1 Career mode

All features active. All three resource sparklines available. All game action types appear in the timeline.

### 7.2 Science mode

- No funds, no reputation, no contracts, no strategies
- Resource budget summary shows only science
- Sparkline shows only science
- Game action types related to funds/reputation/contracts/strategies never appear (the ledger won't contain them)
- Milestones still appear but without fund/rep awards

### 7.3 Sandbox mode

- No game actions at all (empty ledger, no resources to track)
- Resource budget summary hidden
- Sparkline toggle hidden
- Timeline shows only recording lifecycle events, part events, segment events, and flag events
- The timeline is still useful: it shows when recordings launched, what part events occurred, and the chronological structure of the player's mission history

---

## 8. What the Timeline Does Not Do

These are explicit non-goals for Phase 9:

- **Individual event deletion**: the timeline is read-only. The delete buttons on legacy `GameStateEvent` entries are removed. If deletion is ever needed, it goes through a future undo system.
- **Sorting by columns**: the timeline is always sorted by UT. The Game Actions window's sort-by-type/description/status columns do not carry forward. Filtering (not sorting) is the tool for finding specific event types.
- **Live UT marker during warp**: the marker jumps on warp exit. Live-updating during warp is a potential future optimization with minimal visual benefit (the entry list doesn't change during warp).
- **"Jump to UT" / "warp to here"**: not in v1. If added later, the timeline would be the natural surface for it.
- **Per-part-event-type filtering**: the filter bar operates on tiers and source categories, not individual event types. Per-type filtering could be added via a context menu or settings panel in the future.
