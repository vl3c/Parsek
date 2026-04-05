# Design: Timeline (Phase 9)

## Problem

Parsek has two independent views of committed data: the **Recordings Manager** (vessel-centric — list of recordings with per-recording controls) and the **Game Actions window** (resource-centric — flat list of ledger actions with sorting and deletion). Neither answers the question the player actually has: *what happened, and what will happen, in chronological order across all systems?*

A player with 12 recordings spanning 3 missions cannot currently see that a milestone at UT 5000 funded a facility upgrade at UT 5200 which unlocked the tech node at UT 5400 that made mission 3's vessel buildable. The Recordings Manager shows trajectories. The Game Actions window shows resource transactions. Neither shows the causal chain.

The timeline is a unified, chronological view of all committed events — recordings, game actions, part events, milestones — presented sorted by UT with significance tiers and filtering. It is a **read-only query layer**: it does not own data. Recordings, game actions, and milestones remain in their respective systems; the timeline pulls from all of them.

## Mental Model

The timeline is Parsek's equivalent of `git log --all --oneline`. Every committed event across every system appears as a single entry with a universal time, a type, and a one-line description. The player sees the full committed history at all times:

- Everything **before** the current UT has already played out (vessels spawned, resources applied, milestones credited).
- Everything **after** the current UT will play out as ghosts when the player advances.
- There are no branches and no hidden future — the player recorded that future and committed it.

```
past ────────────────── NOW ─────────────────── future
  completed events        │        upcoming events
  (full color)            │        (dimmed)
                          │
  ┌─ UT 500  Launch "Mun Explorer"
  ├─ UT 510  Engine ignition (Mainsail)
  ├─ UT 600  Milestone: First orbit (Kerbin) +5000 funds
  ├─ UT 610  Contract complete: Orbit Kerbin +8000 funds
  ├─ UT 800  Facility upgrade: Runway → Lv.2
  │
  ├─ UT 1200 ◄── current UT
  │
  ├─ UT 1500 Launch "Station Core"              (dimmed)
  ├─ UT 2000 Docking: Station Core + Hab Module (dimmed)
  └─ UT 3000 Contract complete: Build Station   (dimmed)
```

## Data Model

### TimelineEntry

A normalized event shape that unifies all source systems. This is a **view object** — constructed on demand from source data, never persisted.

```
TimelineEntry
    double        UT              // universal time of the event
    TimelineEntryType Type        // discriminator (see below)
    string        DisplayText     // one-line human-readable description
    TimelineSource Source         // which system produced this entry
    SignificanceTier Tier         // T1, T2, or T3
    Color         DisplayColor    // green/red/white (earnings/spendings/neutral)
    string        RecordingId     // source recording ID, null for KSC-only actions
    string        VesselName      // vessel name if applicable, null otherwise
    bool          IsEffective     // false if zeroed by recalculation (duplicate milestone, etc.)
```

### TimelineEntryType

Discriminator enum covering every event source. Grouped by origin system:

```
enum TimelineEntryType
{
    // Recording lifecycle
    RecordingStart,         // first trajectory point
    RecordingEnd,           // last trajectory point (or terminal state)
    VesselSpawn,            // ghost vessel materialized at recording start

    // Game actions (1:1 with GameActionType, mapped directly)
    ScienceEarning,
    ScienceSpending,
    FundsEarning,
    FundsSpending,
    ReputationEarning,
    ReputationPenalty,
    MilestoneAchievement,
    ContractAccept,
    ContractComplete,
    ContractFail,
    ContractCancel,
    KerbalAssignment,
    KerbalHire,
    KerbalRescue,
    KerbalStandIn,
    FacilityUpgrade,
    FacilityDestruction,
    FacilityRepair,
    StrategyActivate,
    StrategyDeactivate,
    FundsInitial,
    ScienceInitial,
    ReputationInitial,

    // Part events (grouped — individual PartEventType exposed in detail view)
    PartStaging,            // Decoupled, FairingJettisoned, ShroudJettisoned
    PartDestruction,        // Destroyed
    PartEngine,             // EngineIgnited, EngineShutdown, EngineThrottle
    PartParachute,          // ParachuteDeployed, ParachuteSemiDeployed, ParachuteCut, ParachuteDestroyed
    PartDocking,            // Docked, Undocked
    PartDeployable,         // DeployableExtended, DeployableRetracted, GearDeployed, GearRetracted,
                            // CargoBayOpened, CargoBayClosed, LightOn, LightOff, LightBlink*
    PartRCS,                // RCSActivated, RCSStopped, RCSThrottle
    PartRobotics,           // RoboticMotionStarted, RoboticPositionSample, RoboticMotionStopped
    PartThermal,            // ThermalAnimationHot, ThermalAnimationMedium, ThermalAnimationCold
    PartInventory,          // InventoryPartPlaced, InventoryPartRemoved

    // Segment events (1:1 with SegmentEventType)
    ControllerChange,
    ControllerDisabled,
    ControllerEnabled,
    CrewLost,
    CrewTransfer,
    SegmentPartDestroyed,
    SegmentPartRemoved,
    SegmentPartAdded,
    TimeJump,

    // Chain/ghost windows
    GhostChainWindow,      // duration entry: vessel untouchable while ghost chain active

    // Flag events
    FlagPlanted,
}
```

### TimelineSource

```
enum TimelineSource
{
    Recording,      // RecordingStore (trajectory lifecycle, part/segment/flag events)
    GameAction,     // Ledger (resource transactions, contracts, milestones, kerbals, etc.)
    Derived,        // Computed by timeline builder (ghost chain windows)
}
```

### Entry Collection

The timeline is rebuilt on demand by `TimelineBuilder.Build()` — a pure function that takes the current committed state and returns a sorted list of `TimelineEntry`. It has three collectors and a merge step:

**1. Recording Collector** — iterates `RecordingStore.CommittedRecordings`:
- Emits `RecordingStart` at `rec.StartUT` with vessel name and terminal state
- Emits `RecordingEnd` at `rec.EndUT` with terminal description (recovering, destroyed, orbiting, etc.)
- Emits `VesselSpawn` at `rec.StartUT` if recording will spawn a ghost
- Iterates `rec.PartEvents` — maps each `PartEventType` to the grouped `TimelineEntryType` (e.g., `Decoupled` → `PartStaging`), constructs display text from part name + event description
- Iterates `rec.SegmentEvents` — maps 1:1, constructs display text from segment event details
- Iterates `rec.FlagEvents` — emits `FlagPlanted` entries
- For chain recordings: computes ghost chain duration windows (first segment StartUT to last segment EndUT) and emits `GhostChainWindow` entries

**2. Game Action Collector** — iterates `Ledger.Actions`:
- Maps each `GameAction` directly to a `TimelineEntryType` (the enum values mirror `GameActionType`)
- Uses `GameActionDisplay.GetDescription()` for display text
- Uses `GameActionDisplay.GetColor()` for display color
- Copies `action.Effective` to `entry.IsEffective`
- Copies `action.RecordingId` to `entry.RecordingId`

**3. Derived Collector** — computes entries not directly stored anywhere:
- Ghost chain windows: for each chain, emit a single duration entry showing "Vessel X: ghost UT start–end"

**4. Merge** — concatenate all entries, sort by UT ascending (stable sort preserving collector order for ties at same UT).

### Grouping Key

Each entry can be associated with a recording via `RecordingId`. This enables hierarchical collapse: the timeline can group all entries belonging to a recording under a collapsible header showing the recording's vessel name and UT range.

For game actions not associated with a recording (KSC spendings where `RecordingId == null`), they appear as top-level ungrouped entries labeled "KSC" or with the specific facility/action context.

## Significance Tiers

Every `TimelineEntryType` belongs to exactly one tier. The tier controls default visibility.

### T1 — Always Visible

High-level mission structure. A T1-only view shows the skeleton of the player's career.

| Entry Type | Rationale |
|---|---|
| RecordingStart | Mission launch — fundamental timeline anchor |
| RecordingEnd | Mission conclusion — terminal state matters |
| VesselSpawn | Ghost materialization — directly observable |
| MilestoneAchievement | One-time progression gate, often with large rewards |
| ContractComplete | Mission objective achieved |
| ContractFail | Mission objective failed (including deadline expiry) |
| FacilityUpgrade | KSC progression — enables new capabilities |
| FacilityDestruction | KSC regression — disables capabilities |
| KerbalHire | Roster expansion — funds spent |
| CrewLost | Kerbal death — irreversible |
| GhostChainWindow | Explains why a vessel is untouchable |
| FundsInitial | Career start — baseline reference |
| ScienceInitial | Career start |
| ReputationInitial | Career start |

### T2 — Visible on Expand or Filter

Significant vessel events and resource transactions. Visible when the player expands a recording's entry group or enables a type filter.

| Entry Type | Rationale |
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
| KerbalRescue | Crew recovery |
| KerbalStandIn | Replacement crew — explains roster changes |
| FacilityRepair | KSC restoration |
| StrategyActivate | Policy change — reward diversion begins |
| StrategyDeactivate | Policy change — diversion ends |
| CrewTransfer | Crew movement between parts |
| FlagPlanted | Player milestone marker |
| ControllerDisabled | Loss of control — critical |
| ControllerEnabled | Control restored |

### T3 — Visible on Explicit Request

Detailed part-level telemetry and low-signal events. Only shown when the player explicitly requests "show all" or filters to a specific part event type.

| Entry Type | Rationale |
|---|---|
| PartEngine (throttle changes) | Per-frame noise — only meaningful in aggregate |
| PartParachute (cut/destroyed/semi-deploy) | Rare, low visual significance |
| PartDestruction | Individual part loss (vessel-level destruction is T1 via RecordingEnd) |
| PartDeployable | Solar panel/antenna/gear/light/cargo bay state changes |
| PartRCS | RCS activation/throttle — high frequency, low signal |
| PartRobotics | Robotic motion samples — continuous, low signal |
| PartThermal | Thermal animation state — derived from flight conditions |
| PartInventory | Inventory part placement/removal |
| ControllerChange | Probe core switch — rare, technical |
| SegmentPartDestroyed | Part destroyed without vessel split |
| SegmentPartRemoved | Part removed (inventory) |
| SegmentPartAdded | Part added (inventory) |
| TimeJump | Discrete UT skip — technical metadata |

### Visibility Rules

- **Default view**: T1 only. Clean, high-level career overview.
- **Expanded recording**: T1 + T2 for that recording's entries. Shows mission-level detail.
- **Filter active**: Shows entries matching the active filter regardless of tier.
- **"Show all" toggle**: T1 + T2 + T3. Full telemetry view.
- **Ineffective entries** (`IsEffective == false`): shown with strikethrough or heavy dimming in any view. These are duplicate milestones, duplicate contract completions, etc. — the player should see they exist but know they had no effect.

## UI Design

### Window Structure

The timeline replaces the current Game Actions window. The Game Actions button in the main Parsek window becomes "Timeline" and opens the timeline sub-window instead.

The resource budget summary (funds/science/reputation reserved vs. available) moves to the top of the timeline window — it remains important context and was the primary content of the old Actions window.

```
┌─ Timeline ──────────────────────────────────────────┐
│                                                     │
│  Funds: 42,000 available (85,000 committed / 127k)  │
│  Science: 120 available (45 committed / 165 total)   │
│  Rep: 38.2                                          │
│                                                     │
│  [T1 ▼] [T2] [T3]  [Recordings ▼] [Actions ▼]     │
│  ─────────────────────────────────────────────────── │
│                                                     │
│  UT 500   ▶ Mun Explorer (UT 500–3200)          [⟲] │
│  UT 600     Milestone: First orbit +5000 funds      │
│  UT 610     Contract: Orbit Kerbin +8000 funds      │
│                                                     │
│  UT 800   Facility: Runway → Lv.2 -18000            │
│                                                     │
│  ──────── UT 1200 (now) ────────────────────────── │
│                                                     │
│  UT 1500  ▶ Station Core (UT 1500–4000)         [⟲] │  ← dimmed
│  UT 2000    Docked: Station Core + Hab Module       │  ← dimmed
│  UT 3000    Contract: Build Station +25000          │  ← dimmed
│                                                     │
│  ▁▁▂▃▃▅▆▆▅▃▂▂▃▅▇▇ Funds                            │
│  ▁▁▁▂▂▃▃▃▃▃▃▃▃▃▃▃ Science                          │
│                                                     │
│  [Close]                                            │
└─────────────────────────────────────────────────────┘
```

### Layout Elements

**Resource Budget** (top) — same as current `DrawResourceBudget()`, showing reserved vs. available for funds, science, reputation. Warnings for over-commitment in red.

**Filter Bar** — row of toggle buttons:
- **Tier toggles**: T1 (always on by default), T2, T3. Clicking T2 shows T1+T2. Clicking T3 shows all.
- **Source filters** (dropdown or toggle): Recordings, Actions, Part Events. Allow hiding entire source categories.
- No per-type granularity in the filter bar — that level of detail goes into a future context menu or settings panel if needed.

**Current-UT Divider** — horizontal line with "UT {value} (now)" label. Entries above are past (full color). Entries below are future (dimmed text and color, not hidden). The divider is a visual anchor — the list does not auto-scroll during gameplay, only repositions on explicit user action (rewind, warp exit).

**Entry List** — vertical scrollable list. Each entry row contains:
- **UT column** — formatted via `KSPUtil.PrintDateCompact(ut)`. Fixed width.
- **Icon or color dot** — small indicator matching `DisplayColor` (green/red/white).
- **Description** — the `DisplayText` string. Fills remaining width.
- **Rewind button** `[⟲]` — only on RecordingStart entries. Triggers rewind to that recording's launch quicksave (same as existing rewind button in Recordings Manager).

**Recording Group Headers** — when a recording has multiple child entries (part events, segment events, game actions), its `RecordingStart` entry acts as a collapsible group header:
- Collapsed (default for T2/T3 content): shows recording name + UT range + entry count badge
- Expanded: shows child entries indented beneath the header
- T1 entries within a recording are always visible regardless of collapse state
- Expand/collapse toggle is the `▶`/`▼` arrow on the recording row

**Ineffective Entries** — entries where `IsEffective == false` (duplicate milestones, duplicate contract completions) are rendered with:
- Strikethrough text style
- Gray color override regardless of earning/spending color
- Tooltip or parenthetical "(duplicate)" suffix

**Future Entries** — entries with UT > current UT:
- Text color at 50% alpha (dimmed but legible)
- No interaction differences — rewind buttons still work (they rewind to before, which is valid)

### Cross-Link with Recordings Manager

- Clicking a recording group header in the timeline selects that recording in the Recordings Manager (if open) and scrolls it into view.
- Selecting a recording in the Recordings Manager scrolls the timeline to that recording's first entry and briefly highlights it.
- Both views stay open simultaneously — they are complementary, not exclusive.

Implementation: a shared `SelectedRecordingId` property on `ParsekUI`. Setting it from either view notifies the other. Timeline uses it to highlight/scroll; Recordings Manager uses it to select.

### Replacing the Game Actions Window

The transition is a replacement, not a merge:

- `showActionsWindow` → `showTimelineWindow`
- `DrawActionsWindowIfOpen()` → `DrawTimelineWindowIfOpen()`
- `DrawActionsWindow()` → `DrawTimelineWindow()`
- `actionsWindowRect` → `timelineWindowRect`
- `actionsScrollPos` → `timelineScrollPos`
- `ActionsSortColumn` enum — removed. Timeline is always sorted by UT. The sort-by-type/description/status columns in the current actions window are superseded by the filter bar.
- `BuildSortedActionEvents()` — replaced by `TimelineBuilder.Build()`
- Delete buttons on individual action entries — removed. The timeline is read-only. Action deletion (if ever needed) happens through the Recordings Manager or a future undo system.

The "Retired Kerbals" section at the bottom of the current Actions window moves to the timeline as filtered entries (KerbalStandIn type with retired status) rather than a separate section.

## Timeline Operations

### Rewind

Each `RecordingStart` entry displays a rewind button `[⟲]`. Clicking it triggers the same rewind flow as the existing button in the Recordings Manager:

1. Confirm via dialog (existing `MergeDialog` pattern)
2. Load the recording's `RewindSaveFileName` quicksave
3. `RecordingStore.ResetAllPlaybackState()`
4. `LedgerOrchestrator.RecalculateAndPatch()` to reconcile state at the new UT

No new rewind infrastructure is needed. The timeline simply provides a new surface for invoking existing rewind.

### Fast-Forward

No explicit fast-forward button in the timeline. The player advances time normally (warp, physics time). The timeline's current-UT divider updates on warp exit (not live during warp — see below).

If a future phase adds "jump to UT" functionality, the timeline would be the natural place for a "warp to here" action on future entries.

### Warp Behavior

Per the roadmap: the current-UT marker does **not** live-update during time warp. It jumps to the correct position on warp exit, when game state is recalculated and vessels spawn. This avoids per-frame timeline rebuilds during warp.

Implementation: `TimelineBuilder.Build()` caches its result. Cache is invalidated on:
- Warp exit (`onTimeWarpRateChanged` when rate → 1)
- Recording commit (`OnRecordingCommitted`)
- Rewind completion
- Scene change
- Ledger mutation (KSC spending)

The current-UT value displayed in the divider reads `Planetarium.GetUniversalTime()` on each draw call (cheap), but the entry list itself is only rebuilt on cache invalidation.

## Resource Sparkline

### Concept

A small sparkline graph running along the bottom of the timeline window showing funds, science, and reputation over time. The recalculation walk already computes running balances at every game action — the sparkline exposes these values visually.

### Data Source

The `RecalculationEngine` walk produces a sequence of `(UT, runningFunds, runningScience, runningRep)` tuples — one per game action in the sorted list. This is the sparkline's data source.

To expose this data without modifying the recalculation engine's pure computation model:

- Add `List<ResourceSnapshot> ResourceHistory` to `RecalculationEngine` as an output field (alongside the existing derived fields on `GameAction`).
- `ResourceSnapshot` is a simple struct: `{ double UT; double Funds; double Science; float Reputation; }`
- The walk appends a snapshot after processing each action (or after each action that changes a balance, as an optimization).
- `TimelineBuilder.Build()` reads this history and passes it to the sparkline renderer.

### Rendering

- Three thin horizontal sparkline strips (one per resource), stacked vertically at the bottom of the timeline window.
- Each strip spans the full UT range of the timeline (min UT of any entry to max UT).
- Y-axis auto-scaled to the min/max of that resource across the timeline.
- Color: funds = yellow/gold, science = cyan/blue, reputation = orange.
- Current-UT position marked with a vertical hairline.
- Toggled on/off from the filter bar. Off by default (avoids visual noise until the player wants it).

### Implementation

Sparkline rendering uses `GUI.DrawTexture` with a procedurally generated `Texture2D`. The texture is regenerated on cache invalidation (same trigger as timeline rebuild). At typical timeline lengths (dozens of game actions), the snapshot list is small and texture generation is trivial.

The sparkline width maps to the timeline window width. Each snapshot maps to an X pixel via linear interpolation of UT. Y maps linearly from resource value to strip height (e.g., 30 pixels tall).

Hover interaction (future): mouse X position → UT → interpolated resource value → tooltip showing exact values. Not in v1.

## Implementation Plan

### Phase 9a — Data Model and Builder

**Goal**: `TimelineBuilder.Build()` produces a correct sorted list of `TimelineEntry` from all sources.

**Files changed**:
- New: `Source/Parsek/Timeline/TimelineEntry.cs` — entry struct + enums
- New: `Source/Parsek/Timeline/TimelineBuilder.cs` — pure static builder, three collectors + merge
- New: `Source/Parsek/Timeline/TimelineEntryDisplay.cs` — display text and color resolution (delegates to `GameActionDisplay` for game action entries, has its own logic for recording/part/segment entries)

**Files read (no changes)**:
- `RecordingStore.cs` — iterate committed recordings, read PartEvents/SegmentEvents/FlagEvents
- `Ledger.cs` — iterate actions
- `GameActionDisplay.cs` — reuse descriptions and colors for game action entries
- `Recording.cs` — read fields for RecordingStart/End entries

**Tests**:
- `TimelineBuilderTests` — given a set of recordings and ledger actions, verify entry count, types, UT ordering, tier assignments, recording ID linkage, effective flags
- Test that entries from all three sources appear in correct UT order
- Test tier classification for every `TimelineEntryType`
- Test that ineffective game actions produce entries with `IsEffective == false`

**Scope**: ~300 lines of production code, ~400 lines of tests.

### Phase 9b — Timeline UI (replaces Actions window)

**Goal**: Timeline window renders the entry list with current-UT divider, tier filtering, recording group collapse, and resource budget.

**Files changed**:
- `ParsekUI.cs` — replace `DrawActionsWindow*` methods with `DrawTimelineWindow*`. Move resource budget to timeline. Add tier filter state, collapse state, cross-link selected recording ID.
- New: `Source/Parsek/Timeline/TimelineRenderer.cs` — extracted rendering helpers if `ParsekUI.cs` gets too large (optional; may keep inline if manageable)

**Dependencies**: Phase 9a (needs `TimelineBuilder`).

**Details**:
- Cache `TimelineBuilder.Build()` result in a field; invalidate on the triggers listed above
- Filter state: `bool showT2`, `bool showT3` (T1 always on)
- Collapse state: `HashSet<string> expandedRecordingIds` — recording IDs whose child entries are visible
- Current-UT divider: find insertion index in sorted entry list, draw separator row
- Future entries: apply 50% alpha color modulation
- Ineffective entries: gray + strikethrough style
- Rewind buttons: delegate to existing `ParsekFlight.RewindToRecording()` (or equivalent)
- Cross-link: `selectedRecordingId` shared with Recordings Manager drawing code

**Scope**: ~500 lines (net change from removing Actions window code and adding Timeline code).

### Phase 9c — Resource Sparkline

**Goal**: Optional sparkline overlay showing funds/science/reputation over time.

**Files changed**:
- `RecalculationEngine.cs` — add `ResourceSnapshot` struct and `ResourceHistory` list, populate during walk
- `ParsekUI.cs` (or `TimelineRenderer.cs`) — sparkline rendering at bottom of timeline window
- New: `Source/Parsek/Timeline/SparklineRenderer.cs` — texture generation and drawing (optional extraction)

**Dependencies**: Phase 9b (needs timeline window), Phase 8 complete (needs recalculation engine).

**Details**:
- `ResourceSnapshot { double UT; double Funds; double Science; float Reputation; }` appended after each balance-changing action in the walk
- Texture regenerated on timeline cache invalidation
- Three strips: 30px tall each, full window width
- Toggle button in filter bar: "Resources" on/off
- No hover interaction in v1

**Scope**: ~200 lines (snapshot collection ~30, rendering ~170).

### Phase 9d — Cross-Link and Polish

**Goal**: Bidirectional cross-linking between Timeline and Recordings Manager. Final polish.

**Files changed**:
- `ParsekUI.cs` — Recordings Manager selection handler sets `selectedRecordingId`, timeline highlights/scrolls to it. Timeline recording header click sets `selectedRecordingId`, Recordings Manager scrolls to it.

**Dependencies**: Phase 9b.

**Details**:
- `selectedRecordingId` is a `string` field on `ParsekUI`, null when nothing selected
- Timeline: on `RecordingStart` entry click → set `selectedRecordingId`
- Recordings Manager: on recording row click → set `selectedRecordingId` (may already exist for other reasons)
- Each view: if `selectedRecordingId` changed since last frame, scroll to matching entry/row and apply brief highlight (fade-out over ~1 second)

**Scope**: ~100 lines.

### Build Order Summary

| Phase | Depends On | Delivers |
|-------|-----------|----------|
| 9a | Phase 8 (ledger/recalculation complete) | Data model, builder, tests |
| 9b | 9a | Timeline UI replacing Actions window |
| 9c | 9b | Resource sparkline |
| 9d | 9b | Cross-link with Recordings Manager |

9c and 9d are independent of each other and can be built in either order or in parallel.

## Design Decisions

**Why replace the Actions window instead of adding a separate window?** The timeline subsumes everything the Actions window does (showing game actions sorted by time) and adds recording events, part events, and visual structure. Keeping both would create redundancy and confusion about which view to use.

**Why not sort by anything other than UT?** The timeline's value proposition is chronological context — seeing causal chains across systems. Sorting by type or description destroys the chronological view that makes the timeline useful. The old Actions window's multi-column sort made sense for a flat table of homogeneous actions; the timeline's heterogeneous entries are only meaningful in time order. Filtering (not sorting) is the tool for finding specific event types.

**Why group part events into categories instead of 1:1 mapping?** There are 35 `PartEventType` values. Exposing all of them as separate `TimelineEntryType` values would make the tier table and filter bar unwieldy. Grouping by function (staging, engines, parachutes, docking, deployables, RCS, robotics, thermal, inventory) keeps the type count manageable while still allowing the tier system to assign appropriate visibility. The detail view within an expanded entry still shows the specific `PartEventType`.

**Why is the timeline cache-invalidated rather than incrementally updated?** The entry list is small (hundreds to low thousands of entries for a typical career). Full rebuild from source data is simpler, avoids incremental-update bugs, and is fast enough for the invalidation triggers (all of which are infrequent user-initiated actions, not per-frame events).

**Why no live UT marker during warp?** Live-updating the marker during warp would require per-frame divider repositioning. The entry list doesn't change during warp (no new commits happen), so the visual benefit is minimal. The marker jumps to the correct position on warp exit when the player can actually interact with the timeline. Live-updating is a potential future optimization.

**Why read-only?** The timeline is a query layer. Allowing edits (delete action, reorder, etc.) would require the timeline to own or mutate data it doesn't own, creating synchronization complexity. Edits to committed data happen through the systems that own that data (Recordings Manager for recordings, future undo system for game actions). The one exception — rewind — is an operation on the game state, not on the timeline data, and already exists in the Recordings Manager.
