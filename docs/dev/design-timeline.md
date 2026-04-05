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
    SignificanceTier  Tier            // T1 or T2
    Color             DisplayColor    // green (earning), red (spending), white (neutral)
    string            RecordingId     // source recording ID; null for KSC-only actions
    string            VesselName      // vessel name if applicable; null otherwise
    bool              IsEffective     // false if zeroed by recalculation (duplicate milestone, etc.)
```

No `DetailType` field — part events are not shown in the timeline, so sub-tier routing is unnecessary.

Ineffective entries (`IsEffective == false`) are demoted one tier: T1→T2. A duplicate milestone only appears at Detail level. Applied after initial tier assignment from type.

### TimelineEntryType

```
enum TimelineEntryType
{
    // Recording lifecycle
    RecordingStart,         // mission launch
    RecordingEnd,           // mission conclusion (terminal state)
    VesselSpawn,            // ghost vessel materialized

    // Game actions (1:1 with GameActionType)
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

    // Derived
    GhostChainWindow,       // duration entry: vessel ghosted for a UT range

    // Legacy (pre-ledger game state events from MilestoneStore)
    LegacyEvent,
}
```

28 types total. No part events, segment events, or flag events — those remain in the Recordings Manager.

### TimelineSource

```
enum TimelineSource
{
    Recording,      // RecordingStore — recording lifecycle events
    GameAction,     // Ledger — resource transactions, contracts, milestones, etc.
    Legacy,         // MilestoneStore — pre-ledger game state events
    Derived,        // Computed by timeline builder (ghost chain windows)
}
```

## Entry Collection

### TimelineBuilder.Build() Signature

```csharp
internal static List<TimelineEntry> Build(
    IReadOnlyList<Recording> committedRecordings,
    IReadOnlyList<GameAction> ledgerActions,
    IReadOnlyList<Milestone> milestones,        // legacy committed events
    uint currentEpoch,
    double currentUT)
```

Three collectors, then merge. Uncommitted events not shown.

### 1. Recording Collector

Iterates `committedRecordings`:
- Emits `RecordingStart` at `rec.StartUT` with vessel name
- Emits `RecordingEnd` at `rec.EndUT` with terminal state from `rec.TerminalStateValue` (e.g., "Recovering", "Destroyed", "Orbiting Kerbin"). Null terminal state → "End"
- Emits `VesselSpawn` at `rec.StartUT` if `rec.PlaybackEnabled` and recording is not a mid-chain segment
- Skips hidden recordings (`rec.Hidden`) unless a "show hidden" flag is set
- Does **not** iterate `rec.PartEvents`, `rec.SegmentEvents`, or `rec.FlagEvents` — those are vessel telemetry, not career events

### Ghost Chain Window Computation

For each distinct `ChainId` among committed recordings (branch 0 only):
1. Collect all recordings with that ChainId and ChainBranch == 0
2. Sort by ChainIndex ascending
3. `windowStart` = earliest `StartUT` across all members
4. `windowEnd` = latest `EndUT` across all members
5. `vesselName` = first member's `VesselName`
6. Emit one `GhostChainWindow` entry at `windowStart` with display text: "`vesselName`: ghost UT `windowStart`–`windowEnd`"

### 2. Game Action Collector

Iterates `ledgerActions`:
- Maps `action.Type` to the matching `TimelineEntryType` (1:1 mapping)
- Uses `GameActionDisplay.GetDescription(action)` for display text, **except**:
  - `ScienceInitial` → `"Starting science: {action.InitialScience:F1}"`
  - `ReputationInitial` → `"Starting reputation: {action.InitialReputation:F0}"`
- Uses `GameActionDisplay.GetColor(action.Type)` for display color
- Copies `action.Effective` → `entry.IsEffective`
- Copies `action.RecordingId` → `entry.RecordingId`
- If `RecordingId` is set, resolves vessel name from `committedRecordings` for display context

### 3. Legacy Collector

Iterates `milestones` for the current epoch:
- For each milestone where `m.Committed && m.Epoch == currentEpoch`:
  - Iterates `m.Events`, skipping `GameStateStore.IsMilestoneFilteredEvent(e.eventType)`
  - Uses `GameStateEventDisplay.GetDisplayCategory` + `GetDisplayDescription` for display text
  - Type = `TimelineEntryType.LegacyEvent`, Source = `TimelineSource.Legacy`
  - All legacy entries are T2

### Merge

Concatenate all entries from all three collectors. Stable sort by UT ascending.

## Tier Assignment

Two tiers only (part events removed, T3 eliminated):

**T1 — Overview:**
`RecordingStart`, `RecordingEnd`, `VesselSpawn`, `MilestoneAchievement`, `ContractComplete`, `ContractFail`, `FacilityUpgrade`, `FacilityDestruction`, `KerbalHire`, `GhostChainWindow`, `FundsInitial`, `ScienceInitial`, `ReputationInitial`

**T2 — Detail:**
`ScienceEarning`, `ScienceSpending`, `FundsEarning`, `FundsSpending`, `ReputationEarning`, `ReputationPenalty`, `ContractAccept`, `ContractCancel`, `KerbalAssignment`, `KerbalRescue`, `KerbalStandIn`, `FacilityRepair`, `StrategyActivate`, `StrategyDeactivate`, `LegacyEvent`

**Demotion:** if `!IsEffective`, bump T1→T2. (T2 entries that are ineffective remain T2 — there's no T3 to demote to.)

## UI Implementation

### File Structure

The refactor-3 branch extracted the Actions window from `ParsekUI.cs` into `UI/ActionsWindowUI.cs`. The timeline replaces this extracted class:

- **Remove**: `Source/Parsek/UI/ActionsWindowUI.cs` (entire file)
- **Remove**: `actionsUI` field and `DrawActionsWindowIfOpen` delegate in `ParsekUI.cs`
- **Add**: `Source/Parsek/UI/TimelineWindowUI.cs` — new class following the same extracted-window pattern (constructor takes `ParsekUI` parent, `DrawIfOpen(Rect)` entry point)
- **Update**: `ParsekUI.cs` — replace `actionsUI` with `timelineUI`, delegate to `timelineUI.DrawIfOpen`

### State Variables

`TimelineWindowUI` owns all timeline state (mirrors the `ActionsWindowUI` pattern):
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
private bool showDetail = false;                        // false = Overview (T1), true = Detail (T1+T2)
private bool showRecordingEntries = true;
private bool showActionEntries = true;
// Cross-link
private string selectedRecordingId;

// Styles
private GUIStyle timelineGrayStyle, timelineWhiteStyle, timelineGreenStyle, timelineRedStyle;
private GUIStyle timelineStrikethroughStyle;   // for ineffective entries
private GUIStyle timelineDimStyle;             // for future entries (50% alpha)
```

### Tier Selector

Two buttons, not three (T3 eliminated with part events):

```
[Overview]  [Detail]
```

`showDetail` boolean. Entry visible if `entry.Tier == T1` (always) or `entry.Tier == T2 && showDetail`.

### Rewind Button

The rewind button on `RecordingStart` entries calls `ShowRewindConfirmation(rec)` → confirmation dialog → `RecordingStore.InitiateRewind(rec)`. Same flow as Recordings Manager.

### Epoch Display

Footer shows "Epoch: N (N reverts)" when `MilestoneStore.CurrentEpoch > 0`.

### Retired Kerbals

Footer shows "Retired Stand-ins (N)" as collapsible section. Data: `LedgerOrchestrator.Kerbals?.GetRetiredKerbals()`.

### Cross-Link Scroll-to-Entry

During draw pass, accumulate row heights. Track Y offset of entry matching `selectedRecordingId`. If changed since last frame, set `timelineScrollPos.y`. Brief highlight via fade timer (~1 second).

### Game Mode Handling

- `Game.Modes.CAREER` → all features
- `Game.Modes.SCIENCE_SANDBOX` → science only in budget/sparkline, no fund/rep/contract/strategy entries
- `Game.Modes.SANDBOX` → hide budget, hide sparkline toggle, show only recording lifecycle

## Implementation Plan

### Phase 9a — Data Model and Builder

**Goal**: `TimelineBuilder.Build()` produces a correct sorted list from all three sources.

**New files**:
- `Source/Parsek/Timeline/TimelineEntry.cs` — entry struct, enums
- `Source/Parsek/Timeline/TimelineBuilder.cs` — static builder with three collectors + merge
- `Source/Parsek/Timeline/TimelineEntryDisplay.cs` — display text/color: delegates to `GameActionDisplay` for game actions, `GameStateEventDisplay` for legacy, custom for recording lifecycle and `ScienceInitial`/`ReputationInitial`

**Files read (no changes)**: `RecordingStore.cs`, `Ledger.cs`, `Recording.cs`, `GameActionDisplay.cs`, `MilestoneStore.cs`, `GameStateEventDisplay.cs`

**Tests** (`TimelineBuilderTests.cs`):
- Recordings + ledger actions + legacy milestones → verify entry count, types, UT ordering
- Tier classification for every `TimelineEntryType`
- Ineffective game actions produce `IsEffective == false` and are demoted T1→T2
- Legacy events from MilestoneStore appear at T2
- Ghost chain window computation from multiple chain members
- Empty inputs → empty list
- Chain recordings with multiple branches → only branch 0 produces ghost chain windows
- Hidden recordings skipped unless flag set
- `ScienceInitial` and `ReputationInitial` produce custom display text
- No part events, segment events, or flag events in output

**Scope**: ~300 lines production, ~400 lines tests.

### Phase 9b — Timeline UI (replaces Actions window)

**Goal**: Timeline window with entry list, filter bar, resource budget, footer.

**Files changed**:
- Remove `Source/Parsek/UI/ActionsWindowUI.cs`
- New: `Source/Parsek/UI/TimelineWindowUI.cs` — timeline window (replaces ActionsWindowUI)
- `ParsekUI.cs` — replace `actionsUI` field with `timelineUI`, update delegate and button label

**Dependencies**: Phase 9a.

**Details**:
- Cache `TimelineBuilder.Build()` result; invalidate on triggers
- Tier selector: `showDetail` boolean (Overview/Detail)
- Source toggles: `showRecordingEntries`, `showActionEntries`
- Current-UT divider: binary search in cached sorted list
- Future entries: 50% alpha
- Ineffective entries: gray + strikethrough
- Rewind button: `ShowRewindConfirmation(rec)` → `RecordingStore.InitiateRewind(rec)`
- Footer: epoch, retired kerbals, close
- Game mode: hide budget/sparkline in sandbox, science-only in science mode

**Scope**: ~500 lines (remove ~300 Actions window, add ~500 Timeline).

### Phase 9c — Cross-Link and Polish

**Goal**: Bidirectional cross-linking between Timeline and Recordings Manager.

**Files changed**: `ParsekUI.cs` — shared `selectedRecordingId`, scroll-to-entry.

**Dependencies**: Phase 9b.

**Scope**: ~150 lines.

### Build Order

| Phase | Depends On | Delivers |
|-------|-----------|----------|
| 9a | v0.6 (game actions system complete) | Data model, builder, tests |
| 9b | 9a | Timeline UI replacing Actions window |
| 9c | 9b | Cross-link with Recordings Manager |
