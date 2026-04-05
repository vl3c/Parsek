# Design: Timeline (Phase 9) — Implementation Spec

Implementation-level companion to `docs/parsek-timeline-design.md`. That document covers philosophy, player interaction, and design rationale. This document covers data structures, algorithms, file changes, and test plans.

## Data Model

### TimelineEntry

```
TimelineEntry
    double            UT              // universal time of the event
    TimelineEntryType Type            // discriminator (see below)
    string            DisplayText     // one-line human-readable description
    TimelineSource    Source          // Recording, GameAction, Legacy, or Derived
    SignificanceTier  Tier            // T1, T2, or T3
    Color             DisplayColor    // green (earning), red (spending), white (neutral)
    string            RecordingId     // source recording ID; null for KSC-only actions
    string            VesselName      // vessel name if applicable; null otherwise
    bool              IsEffective     // false if zeroed by recalculation (duplicate milestone, etc.)
    PartEventType?    DetailType      // underlying PartEventType for grouped part entries; null otherwise
```

The `DetailType` field solves the sub-tier routing problem: `PartEngine` and `PartParachute` each span two tiers. The builder inspects `DetailType` during construction to assign the correct tier.

### TimelineEntryType

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

    // Part events (grouped — individual PartEventType exposed via DetailType)
    PartStaging,            // Decoupled, FairingJettisoned, ShroudJettisoned
    PartDestruction,        // Destroyed
    PartEngine,             // EngineIgnited, EngineShutdown, EngineThrottle
    PartParachute,          // ParachuteDeployed, ParachuteSemiDeployed, ParachuteCut, ParachuteDestroyed
    PartDocking,            // Docked, Undocked
    PartDeployable,         // DeployableExtended/Retracted, Gear, CargoBay, Light, LightBlink (all 3)
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

    // Derived
    GhostChainWindow,      // duration entry: vessel untouchable while ghost chain active

    // Flag events
    FlagPlanted,

    // Legacy (pre-ledger game state events from MilestoneStore)
    LegacyEvent,
}
```

### TimelineSource

```
enum TimelineSource
{
    Recording,      // RecordingStore — trajectory lifecycle, part/segment/flag events
    GameAction,     // Ledger — resource transactions, contracts, milestones, kerbals, etc.
    Legacy,         // MilestoneStore — pre-ledger game state events
    Derived,        // Computed by timeline builder (ghost chain windows)
}
```

### Sub-Tier Routing

Some grouped part event types span tiers. The builder uses `DetailType` to assign the correct tier:

| Group | T2 members | T3 members |
|-------|-----------|-----------|
| PartEngine | EngineIgnited, EngineShutdown | EngineThrottle |
| PartParachute | ParachuteDeployed | ParachuteSemiDeployed, ParachuteCut, ParachuteDestroyed |

Implementation: a static method `GetTierForPartEvent(PartEventType detail)` returns the correct tier. The builder calls this instead of a flat type→tier lookup when processing part events.

## Entry Collection

### TimelineBuilder.Build() Signature

The builder accepts data sources as parameters for testability — it does not read global static state:

```csharp
internal static List<TimelineEntry> Build(
    IReadOnlyList<Recording> committedRecordings,
    IReadOnlyList<GameAction> ledgerActions,
    IReadOnlyList<Milestone> milestones,        // legacy committed events
    IReadOnlyList<GameStateEvent> uncommitted,   // current flight session
    uint currentEpoch,
    double currentUT)
```

Four collectors run in sequence, then results are merged by UT:

### 1. Recording Collector

Iterates `committedRecordings`:
- Emits `RecordingStart` at `rec.StartUT` with vessel name
- Emits `RecordingEnd` at `rec.EndUT` with terminal state from `rec.TerminalStateValue` (e.g., "Recovering", "Destroyed", "Orbiting Kerbin", "Stranded"). Null terminal state → "End"
- Emits `VesselSpawn` at `rec.StartUT` if `rec.PlaybackEnabled` is true and the recording is not a mid-chain segment
- Maps each `PartEvent` to a grouped `TimelineEntryType` via a static `MapPartEventType(PartEventType)` method. Stores the original `PartEventType` in `DetailType`. Constructs display text from `partEvent.partName` and event description.
- Maps each `SegmentEvent` 1:1 to `TimelineEntryType`. Constructs display text from `segmentEvent.details`.
- Maps each `FlagEvent` to `FlagPlanted` with display text from flag site name.
- Skips hidden recordings (`rec.Hidden`) unless a "show hidden" flag is set.

### Ghost Chain Window Computation

For each distinct `ChainId` among committed recordings (branch 0 only):
1. Collect all recordings with that ChainId and ChainBranch == 0
2. Sort by ChainIndex ascending
3. `windowStart` = earliest `StartUT` across all members
4. `windowEnd` = latest `EndUT` across all members
5. `vesselName` = first member's `VesselName`
6. Emit one `GhostChainWindow` entry at `windowStart` with display text: "`vesselName`: ghost UT `windowStart`–`windowEnd`"

This reconstructs chain windows from committed recording data without needing runtime `GhostChain` objects (which are built by `ParsekPlaybackPolicy` and don't exist when the timeline builder runs).

### 2. Game Action Collector

Iterates `ledgerActions`:
- Maps `action.Type` to the matching `TimelineEntryType` (1:1 mapping)
- Uses `GameActionDisplay.GetDescription(action)` for display text, **except**:
  - `ScienceInitial` → `"Starting science: {action.InitialScience:F1}"` (no case in GameActionDisplay)
  - `ReputationInitial` → `"Starting reputation: {action.InitialReputation:F0}"` (no case in GameActionDisplay)
- Uses `GameActionDisplay.GetColor(action.Type)` for display color (note: takes `GameActionType`, not `GameAction`)
- Copies `action.Effective` → `entry.IsEffective`
- Copies `action.RecordingId` → `entry.RecordingId`

### 3. Legacy Collector

Iterates `milestones` for the current epoch:
- For each milestone where `m.Committed && m.Epoch == currentEpoch`:
  - Iterates `m.Events`, skipping `GameStateStore.IsMilestoneFilteredEvent(e.eventType)` (resource events, crew status changes)
  - Maps each to `TimelineEntryType.LegacyEvent`
  - Uses `GameStateEventDisplay.GetDisplayCategory(e.eventType)` as prefix and `GetDisplayDescription(e)` as description
  - All legacy entries are T2 (significant but not mission-structure-defining)
  - Source = `TimelineSource.Legacy`

### 4. Uncommitted Collector

Iterates `uncommitted` events after the last milestone's EndUT in the current epoch:
- Same filtering as legacy collector (`IsMilestoneFilteredEvent` skips resource events and crew status)
- Same display logic (`GameStateEventDisplay`)
- Marked with `TimelineEntryType.LegacyEvent` and `TimelineSource.Legacy`
- These entries are visually distinct (see UI section) but structurally identical to legacy entries

### Merge

Concatenate all entries from all four collectors. Stable sort by UT ascending.

## Significance Tier Tables

See `docs/parsek-timeline-design.md` §4 for the full tier classification tables. Key implementation detail: the tier is assigned during entry construction, not during rendering. The `Tier` field on `TimelineEntry` is final once built.

## UI Implementation

### State Variables (replacing Actions window)

Remove:
```csharp
private bool showActionsWindow;
private Rect actionsWindowRect;
private Vector2 actionsScrollPos;
private bool isResizingActionsWindow;
private bool actionsWindowHasInputLock;
private GUIStyle actionsGrayStyle, actionsWhiteStyle, actionsGreenStyle, actionsRedStyle;
private enum ActionsSortColumn { Time, Type, Description, Status }
private ActionsSortColumn actionsSortColumn;
private bool actionsSortAscending;
```

Add:
```csharp
private bool showTimelineWindow;
private Rect timelineWindowRect;
private Vector2 timelineScrollPos;
private bool isResizingTimelineWindow;
private bool timelineWindowHasInputLock;

// Cached timeline data (invalidated on triggers)
private List<TimelineEntry> cachedTimeline;
private bool timelineDirty = true;

// Filter state
private int tierLevel = 1;                              // 1 = T1 only, 2 = T1+T2, 3 = all
private bool showRecordingEntries = true;
private bool showActionEntries = true;
private bool showSparkline = false;

// Collapse state
private HashSet<string> expandedRecordingIds = new HashSet<string>();

// Cross-link
private string selectedRecordingId;

// Styles (lazy-init same as before)
private GUIStyle timelineGrayStyle, timelineWhiteStyle, timelineGreenStyle, timelineRedStyle;
private GUIStyle timelineStrikethroughStyle;   // for ineffective entries
private GUIStyle timelineDimStyle;             // for future entries (50% alpha)
```

### Tier Selector

Three cumulative buttons, not independent toggles:

```
[T1]  [T1+2]  [All]
```

`tierLevel` is 1, 2, or 3. An entry is visible if `entry.Tier <= tierLevel` (T1=1, T2=2, T3=3). This avoids the T1+T3-without-T2 inconsistency that independent booleans would allow.

### Rewind Button

The rewind button on `RecordingStart` entries calls `ShowRewindConfirmation(rec)`, the same method used by the Recordings Manager. This displays a confirmation dialog ("Rewind to 'Vessel Name' launch at UT?") and on confirmation calls `RecordingStore.InitiateRewind(rec)`, which:
1. Initializes `RewindContext` with target UT and reserved resource budgets
2. Copies the quicksave from `Parsek/Saves/` to KSP root `saves/`
3. Pre-processes the save file (strips spawned vessels, winds back UT 10 seconds)
4. Loads via `GamePersistence.LoadGame`
5. Transitions to Space Center scene

No new rewind infrastructure. The timeline provides a new surface for the same flow.

### Uncommitted Events Section

Below the main timeline entry list, a divider labeled "── Uncommitted (current flight) ──" followed by uncommitted entries in normal color. This section only appears when uncommitted events exist and the player is in flight scene.

### Epoch Display

Footer shows "Epoch: N (N reverts)" when `MilestoneStore.CurrentEpoch > 0`, same as current Actions window.

### Retired Kerbals

Footer shows "Retired Stand-ins (N)" as a collapsible section, same content as current Actions window. Data source: `LedgerOrchestrator.Kerbals?.GetRetiredKerbals()`.

### Cross-Link Scroll-to-Entry

IMGUI scroll views have no built-in "scroll to element" API. Implementation approach:

During the draw pass, accumulate row heights. Track the Y offset of the entry matching `selectedRecordingId`. After the draw pass, if `selectedRecordingId` changed since last frame, set `timelineScrollPos.y` to the tracked offset. Apply a brief highlight by tracking a `highlightFadeTimer` that counts down from 1.0 to 0.0 over ~1 second, modulating the background color of the highlighted row.

### Game Mode Handling

**Career**: all features active.
**Science**: resource budget shows only science. Sparkline shows only science strip. Funds/reputation game action types never appear (ledger won't contain them).
**Sandbox**: resource budget hidden. Sparkline toggle hidden. Timeline shows only recording lifecycle, part/segment events, and flag events. Still useful for chronological mission structure.

The resource budget section checks `HighLogic.CurrentGame.Mode`:
- `Game.Modes.CAREER` → show funds, science, reputation
- `Game.Modes.SCIENCE_SANDBOX` → show science only
- `Game.Modes.SANDBOX` → hide budget section entirely

## Resource Sparkline

### Data Source

The `RecalculationEngine` currently has no output mechanism — it is purely side-effecting into registered `IResourceModule` instances. Running balances live inside `FundsModule.runningBalance`, `ScienceModule.runningScience`, and `ReputationModule.runningRep` as private state.

To expose resource history:

1. Add `List<ResourceSnapshot> ResourceHistory` as a public output field on `RecalculationEngine`
2. Add `ResourceSnapshot` struct: `{ double UT; double Funds; double Science; float Reputation; }`
3. After the walk loop processes each action, query module state via existing getter methods (`GetRunningFunds()`, `GetRunningScience()`, `GetRunningReputation()`) and append a snapshot
4. Clear `ResourceHistory` at the start of each `Recalculate()` call

This preserves the engine's pure computation model — the modules remain the source of truth, the history is just a log of their states during the walk.

### Texture Generation

`SparklineRenderer.GenerateTexture(List<ResourceSnapshot> history, float windowWidth, Game.Modes mode)` → `Texture2D`

- Width = window width in pixels (integer)
- Height = 30px per visible resource strip (90px for career, 30px for science mode)
- Each snapshot maps to X via `(snap.UT - minUT) / (maxUT - minUT) * width`
- Each resource value maps to Y via `(value - minValue) / (maxValue - minValue) * stripHeight`
- Colors: funds = `(0.9f, 0.8f, 0.2f)`, science = `(0.3f, 0.8f, 1.0f)`, reputation = `(1.0f, 0.6f, 0.2f)`
- Current-UT hairline = white, 1px wide

Regenerated on timeline cache invalidation. Disposed on regeneration (old texture freed).

## Implementation Plan

### Phase 9a — Data Model and Builder

**Goal**: `TimelineBuilder.Build()` produces a correct sorted list from all four sources.

**New files**:
- `Source/Parsek/Timeline/TimelineEntry.cs` — entry struct, `TimelineEntryType`, `TimelineSource`, `SignificanceTier` enums
- `Source/Parsek/Timeline/TimelineBuilder.cs` — static builder with four collectors + merge
- `Source/Parsek/Timeline/TimelineEntryDisplay.cs` — display text/color resolution: delegates to `GameActionDisplay` for game actions, `GameStateEventDisplay` for legacy events, custom logic for recording/part/segment entries and `ScienceInitial`/`ReputationInitial`

**Files read (no changes)**: `RecordingStore.cs`, `Ledger.cs`, `Recording.cs`, `GameActionDisplay.cs`, `MilestoneStore.cs`, `GameStateStore.cs`, `GameStateEventDisplay.cs`

**Tests** (`TimelineBuilderTests.cs`):
- Given recordings + ledger actions + legacy milestones + uncommitted events → verify entry count, types, UT ordering
- Tier classification for every `TimelineEntryType`, including sub-tier routing for `PartEngine` and `PartParachute`
- Ineffective game actions produce entries with `IsEffective == false`
- Legacy events from MilestoneStore appear at T2
- Uncommitted events appear after committed entries at matching UT
- Ghost chain window computation from multiple chain members
- Empty inputs (no recordings, no ledger, no milestones) → empty list
- Chain recordings with multiple branches → only branch 0 produces ghost chain windows
- Hidden recordings skipped unless flag set
- `ScienceInitial` and `ReputationInitial` produce custom display text

**Scope**: ~400 lines of production code, ~500 lines of tests.

### Phase 9b — Timeline UI (replaces Actions window)

**Goal**: Timeline window renders the entry list with all visual elements described in §5 of the main design doc.

**Files changed**:
- `ParsekUI.cs` — remove `DrawActionsWindow*` methods and related state. Add `DrawTimelineWindow*` methods. Move resource budget. Add tier selector, collapse state, cross-link.
- New: `Source/Parsek/Timeline/TimelineRenderer.cs` — extracted rendering helpers if `ParsekUI.cs` gets too large (optional)

**Dependencies**: Phase 9a.

**Details**:
- Cache `TimelineBuilder.Build()` result; invalidate on: warp exit, recording commit, rewind completion, scene change, ledger mutation
- Tier selector: cumulative `tierLevel` int (1/2/3), not independent booleans
- Collapse state: `HashSet<string> expandedRecordingIds`
- Current-UT divider: binary search in cached sorted list for insertion index
- Future entries: 50% alpha color
- Ineffective entries: gray + strikethrough
- Rewind button: `ShowRewindConfirmation(rec)` → `RecordingStore.InitiateRewind(rec)`
- Uncommitted section: separate divider, normal color, only when events exist
- Footer: epoch display, retired kerbals, close button
- Game mode: hide budget/sparkline in sandbox, science-only in science mode

**Scope**: ~600 lines (net change: remove ~300 lines of Actions window, add ~600 lines of Timeline).

### Phase 9c — Resource Sparkline

**Goal**: Optional sparkline overlay at bottom of timeline window.

**Files changed**:
- `RecalculationEngine.cs` — add `ResourceSnapshot` struct, `ResourceHistory` list, populate during walk via existing module getters
- New: `Source/Parsek/Timeline/SparklineRenderer.cs` — texture generation and `GUI.DrawTexture` rendering

**Dependencies**: Phase 9b (needs timeline window), v0.6 complete (recalculation engine).

**Scope**: ~200 lines (snapshot collection ~30, renderer ~170).

### Phase 9d — Cross-Link and Polish

**Goal**: Bidirectional cross-linking between Timeline and Recordings Manager.

**Files changed**: `ParsekUI.cs` — shared `selectedRecordingId`, scroll-to-entry in both windows.

**Dependencies**: Phase 9b.

**Details**:
- `selectedRecordingId` string field on `ParsekUI`, null when nothing selected
- Timeline: recording header click → set `selectedRecordingId`
- Recordings Manager: recording row click → set `selectedRecordingId`
- Scroll-to-entry via Y offset accumulation during draw pass
- Highlight fade timer (1s countdown)

**Scope**: ~150 lines (includes IMGUI scroll-to-entry calculation).

### Build Order

| Phase | Depends On | Delivers |
|-------|-----------|----------|
| 9a | v0.6 (game actions system complete) | Data model, builder, tests |
| 9b | 9a | Timeline UI replacing Actions window |
| 9c | 9b | Resource sparkline |
| 9d | 9b | Cross-link with Recordings Manager |

9c and 9d are independent and can be built in parallel.
