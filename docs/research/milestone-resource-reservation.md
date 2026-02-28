# Milestone-Resource Reservation Analysis

Deep investigation into how milestones, recordings, resource reservation, and deletion interact — identifying paradoxes, edge cases, and design decisions.

## Architecture

### Two Independent Resource Reservation Sources

Resource budget is computed from two independent sources in `ResourceBudget.ComputeTotal()`:

1. **Recording costs** — net flight impact: `PreLaunchFunds - Points[last].funds`
   - Includes launch cost, in-flight expenses, and earnings (contracts, science)
   - Tracked via `LastAppliedResourceIndex` for partial replay awareness
   - Fully replayed recordings contribute 0

2. **Milestone costs** — game state event costs from unreplayed semantic events
   - `PartPurchased` → funds cost from `detail` field
   - `TechResearched` → science cost from `detail` field
   - `FacilityUpgraded` → funds cost (computed from level delta)
   - Tracked via `LastReplayedEventIndex` for partial replay awareness
   - Fully replayed milestones contribute 0

These are independent. Deleting a recording removes only source #1. Milestones persist.

### Milestone Lifecycle (Decoupled)

Milestones are created at two points:
- **Recording commit** (`RecordingStore.CommitPending()`) — bundles game state events from the period since the last milestone, tagged with the recording's ID as optional metadata
- **Game save** (`ParsekScenario.OnSave()` via `FlushPendingEvents`) — captures any events that happened without a recording commit (e.g., researching tech in R&D without ever launching)

Milestones are **not deleted** when recordings are deleted. They represent independent player actions.

## Scenario Analysis

### Scenario A: Normal Forward Play

```
UT 0:     Player starts career (50000 funds, 0 science)
UT 50:    Researches Basic Rocketry (costs 5 science)
UT 80:    Buys mk1pod.v2 (costs 600 funds)
UT 100:   Records flight (launches, costs 5000, earns 2000 from contract)
UT 200:   Commits recording
```

**State after commit:**
- Recording: PreLaunch=50000, End=47000 → cost = 3000 funds
- Milestone: TechResearched(5 sci) + PartPurchased(600 funds)
- `LastAppliedResourceIndex` = Points.Count-1 (fully applied)
- `LastReplayedEventIndex` = Events.Count-1 (fully applied)
- **Budget: 0 reserved** (everything already applied)

**Result:** No reservation shown during normal play. Correct.

### Scenario B: Delete Recording During Normal Play

Same as A, then player deletes the recording.

**State after deletion:**
- Recording: gone
- Milestone: still exists, still fully applied (LastReplayedEventIndex = 1)
- **Budget: 0 reserved** (milestone is fully applied)

**Result:** No change to resource budget. Player's funds/science unchanged. Correct.

### Scenario C: Go Back in Time, Then Delete Recording

```
UT 200:   Player commits recording + milestone (both fully applied)
          Player reverts to UT 50 (loads earlier save)
```

**State after revert:**
- `LastAppliedResourceIndex` restored from quicksave → -1 (unreplayed)
- `LastReplayedEventIndex` restored from quicksave → -1 (unreplayed)
- Epoch incremented (prevents old-branch events from leaking)
- **Budget: 3000 (recording) + 600 (milestone funds) + 5 (milestone science)**

Player deletes the recording:
- Recording: gone
- Milestone: still exists, still unreplayed
- **Budget: 600 funds + 5 science** (milestone costs only)

**Result:** Milestone events will replay at their original UTs when UT advances past them. The 600 funds for the part purchase and 5 science for the tech research are correctly reserved. Correct.

### Scenario D: Insufficient Resources After Revert

```
UT 0:     Player starts with 400 funds, 10 science
UT 50:    Researches tech (costs 5 science) — player now has 5 science
UT 80:    Buys part (costs 600 funds) — player had earned 1000 funds from contract
UT 100:   Records flight, commits
          Reverts to UT 0 (400 funds, 10 science)
```

**State after revert:**
- Milestone reserves: 600 funds + 5 science
- Available: 400 - 600 = **-200 funds** (negative!)

**Is this a paradox?** No. The UI will display negative available funds. This tells the player they are over-committed. When the milestone replay engine (Phase 2) tries to buy the part at UT 80, it will fail because the player doesn't have 600 funds.

**Handling options (Phase 5 concern):**
1. **Block replay** — skip the event, warn the player
2. **Allow deficit** — apply the event anyway (KSP's own contract system sometimes drives funds negative)
3. **Player dialog** — "You don't have enough funds to replay this action. Skip or force?"

This is a Phase 5 design decision, not a current bug. The current budget display correctly shows the over-commitment.

### Scenario E: Multiple Milestones, Delete Middle Recording

```
UT 0-100:   Milestone 1 (TechResearched), Recording 1 committed
UT 100-200: Milestone 2 (PartPurchased), Recording 2 committed
```

Player deletes Recording 1:
- Milestone 1 survives (has TechResearched)
- Milestone 2 survives (has PartPurchased)
- Recording 1 cost removed from budget
- Milestone costs unchanged

**Result:** Both milestones are independent. No data loss. Correct.

### Scenario F: Player Never Records a Flight

```
UT 50:    Researches Basic Rocketry
UT 80:    Upgrades Launchpad
UT 100:   Game autosaves (OnSave fires)
```

Without FlushPendingEvents, these events would never become milestones. With it:

**OnSave at UT 100:**
- `FlushPendingEvents(100)` checks for events with UT > max(existing milestone EndUTs)
- Finds TechResearched(UT 50) and FacilityUpgraded(UT 80)
- Creates milestone with RecordingId="" (no recording association)
- Milestone is fully applied (LastReplayedEventIndex = Events.Count-1)

**Result:** Events are captured regardless of recording activity. If the player later goes back in time, these events will be available for replay. Correct.

### Scenario G: FlushPendingEvents Called Multiple Times

```
UT 50:    Researches tech
UT 100:   OnSave → FlushPendingEvents creates milestone (UT 0-100)
UT 150:   OnSave → FlushPendingEvents called again
```

Second call: `startUT = max(existing EndUTs) = 100`. No events with UT > 100. Returns null (no-op).

**Result:** Safe to call repeatedly. No duplicate milestones. Correct.

### Scenario H: FlushPendingEvents + Recording Commit Interleaving

```
UT 50:    Researches tech (event in GameStateStore)
UT 100:   Records flight, commits
          → CommitPending calls CreateMilestone("rec1", 100)
          → Milestone created for events UT 0-100 (captures the tech research)
UT 150:   Buys part (event in GameStateStore)
UT 200:   OnSave fires
          → FlushPendingEvents(200)
          → startUT = max(EndUTs) = 100, finds PartPurchased at UT 150
          → Creates milestone for events UT 100-200
```

**Result:** No overlap. Each milestone covers a distinct UT range. The `startUT` watermark mechanism prevents double-capture. Correct.

### Scenario I: Revert Resets New Milestones

```
UT 100:   Player commits recording → Milestone A created (fully applied)
UT 150:   Player researches tech
UT 200:   OnSave → FlushPendingEvents → Milestone B created (fully applied)
          Player reverts to launch at UT 100
```

**State after revert:**
- `RestoreMutableState(node, resetUnmatched: true)` is called
- Milestone A: found in quicksave stateMap → restored to pre-revert state
- Milestone B: NOT in quicksave stateMap (created after launch save) → `LastReplayedEventIndex = -1`
- Epoch incremented

**Result:** Milestone B correctly reset to unreplayed. Its events will replay during forward play. Correct.

### Scenario J: Wipe Recordings vs Wipe All

**"Wipe Recordings" button:**
- Removes all committed recordings (files + metadata)
- Unreserves crew, destroys ghosts
- Does NOT touch milestones
- Recording costs drop to 0
- Milestone costs unchanged

**"Wipe All" button:**
- Does everything "Wipe Recordings" does
- Also calls `MilestoneStore.ClearAll()`
- Both recording and milestone costs drop to 0
- Complete reset of timeline state

**Result:** Player has granular control. Wiping recordings is safe for milestone data. Correct.

## Edge Cases

### Double-Counting Prevention

Raw resource events (FundsChanged, ScienceChanged, ReputationChanged) are excluded from milestones at creation time (`GameStateStore.IsResourceEvent` filter). This prevents double-counting between:
- Recording trajectory deltas (which capture funds/science/rep at each point)
- Milestone semantic events (which have known costs from their `detail` field)

### Epoch Isolation

After a revert, `MilestoneStore.CurrentEpoch` is incremented. New events are stamped with the new epoch. `CreateMilestone` and `FlushPendingEvents` filter by `epoch == CurrentEpoch`, ensuring abandoned-branch events are excluded from new milestones.

### Milestone with Empty RecordingId

Milestones created by `FlushPendingEvents` have `RecordingId = ""`. This is valid and serializes cleanly. The RecordingId field is advisory metadata only — no code path uses it for lookup or deletion after decoupling.

### Partial Replay Awareness

Both recording costs and milestone costs are partial-replay aware:

**Recordings:** If `LastAppliedResourceIndex` is between 0 and Points.Count-1, only the unapplied portion is reserved:
```
totalImpact = PreLaunch - Points[last]
alreadyApplied = PreLaunch - Points[lastApplied]
reserved = totalImpact - alreadyApplied
```

**Milestones:** Only events after `LastReplayedEventIndex` contribute to the budget. Events at or before the index are considered already applied.

### Milestone StartUT Gap After Deletion (Non-Issue)

If milestones are never deleted (only cleared via "Wipe All"), there are no UT gaps. The `startUT` watermark (max of all existing milestone EndUTs) ensures continuous coverage.

If a future feature allows individual milestone deletion, a gap could form. But `CreateMilestone` would then create a milestone covering the gap on the next trigger. No events would be lost because GameStateStore retains all events regardless of milestones.

## Summary of Invariants

1. **Milestones are independent of recordings** — creating/deleting recordings does not create/delete milestones (except that `CommitPending` triggers milestone creation as a convenience)
2. **Resource budget is computed on-the-fly** — no cached state, purely derived from current recordings + milestones
3. **Fully applied = zero cost** — during normal forward play, everything is fully applied, reservation is 0
4. **Events are never lost** — GameStateStore retains all events; milestones are a commitment/grouping layer on top
5. **Epoch isolates branches** — reverted timeline branches don't leak into new milestones
6. **FlushPendingEvents is idempotent** — safe to call multiple times, only creates milestones when new events exist
7. **resetUnmatched on revert** — milestones created after the launch quicksave are reset to unreplayed (-1) on revert
