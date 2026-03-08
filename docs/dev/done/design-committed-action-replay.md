# Design: Committed Action Replay

## Problem

After reverting to an earlier point in the timeline, Parsek correctly deducts reserved resources (funds, science, reputation) from the player's pool via budget deduction. However, **discrete game actions** recorded during committed flights - tech node unlocks, part purchases, facility upgrades - are NOT re-applied. The player pays the resource cost but doesn't receive the action.

Currently:
- Tech: science deducted but node stays locked. Player must re-research, paying science twice.
- Parts: funds deducted but part stays unpurchased. Player must re-buy, paying funds twice.
- Facilities: funds deducted (via trajectory deltas) but facility stays at pre-upgrade level. Player must re-upgrade, paying funds twice.
- Crew: funds deducted but hired crew not re-added to roster.

The existing Harmony patches (`TechResearchPatch`, `FacilityUpgradePatch`) only **block** duplicate actions while milestones are unreplayed. Once `ApplyBudgetDeductionWhenReady` marks milestones as fully replayed (`LastReplayedEventIndex = Events.Count - 1`), the blocks lift and the player can (and must) perform the action again - paying the cost a second time.

The fix for each category follows the same pattern: **replay the committed action programmatically** during budget deduction, rather than just deducting the cost and leaving the action un-done.

### Prior Art: Science Duplication Fix

The science experiment duplication bug (PR #17) was solved with a different approach - Harmony postfix patches on `ResearchAndDevelopment.GetExperimentSubject` / `GetSubjectByID` that inject committed science values into `ScienceSubject.science`. This makes KSP's diminishing returns formula correctly reduce remaining science for experiments already committed on the timeline. The science fix includes:

- `ScienceSubjectPatch` (Harmony postfix) - injects committed science values
- `GameStateStore.committedScienceSubjects` - per-subject max science tracking
- `GameStateStore.originalScienceValues` - baseline tracking for restoration on wipe
- `GameStateRecorder.PendingScienceSubjects` - captures science during recording
- `RecordingStore.CommitPending/CommitTree` - transfers pending to committed store

This design does NOT modify the science fix. Action replay handles tech/parts/facilities/crew - a different problem (discrete actions not performed vs. continuous values not injected).

## Terminology

- **Action replay**: programmatically performing a game action (unlock tech, purchase part, upgrade facility) that was committed in a milestone, restoring the game state to match the committed timeline.
- **Budget deduction**: the existing mechanism in `ParsekScenario.ApplyBudgetDeductionWhenReady()` that deducts reserved resource costs on revert/go-back. This is the natural trigger point for action replay.
- **Rewind resource adjustment**: the separate mechanism in `ParsekScenario.ApplyRewindResourceAdjustment()` that handles differential cost changes after a recording-based rewind. Also needs action replay.
- **Committed action**: a `GameStateEvent` inside a committed `Milestone` that represents a discrete game action (not a resource pool change).

## Mental Model

```
BEFORE (current behavior):

  Revert → Budget deduction deducts funds/science → Milestones marked fully replayed
                                                   → Actions NOT performed
                                                   → Blocking patches lift
                                                   → Player re-performs action (double pay)

AFTER (with action replay):

  Revert → Budget deduction deducts funds/science → Actions replayed programmatically
                                                   → Tech unlocked, parts purchased,
                                                     facilities upgraded, crew hired
                                                   → Blocking patches still active
                                                     (actions already done = no need to block)
                                                   → Milestones marked fully replayed
```

The key insight: action replay should happen **during budget deduction, before milestones are marked as fully replayed**. The blocking patches check unreplayed events via `LastReplayedEventIndex + 1 < Events.Count` (see `MilestoneStore.GetCommittedTechIds()`, `GetCommittedFacilityUpgrades()`). If we replay actions first and THEN mark fully replayed, the window where actions are "committed but not yet performed" doesn't exist.

However, the current flow marks milestones as fully replayed (`LastReplayedEventIndex = Events.Count - 1`) at the same time as budget deduction. We need to:
1. Deduct resources (existing)
2. Replay discrete actions (new)
3. Then mark as fully replayed (existing, but after step 2)

## Phases

### Phase 1: Tech Node Replay

**Why first**: Tech unlocks are the most impactful action - they gate access to parts and the tech tree is central to career progression. The `TechResearchPatch` already exists, confirming the infrastructure is in place.

**KSP API**: `ResearchAndDevelopment.Instance.UnlockProtoTechNode(ProtoTechNode)` or iterate `RDTech` nodes. Need to investigate during planning which API correctly unlocks a tech node programmatically (including granting the parts).

**Data available**: `TechResearched` events have `key = techId` and `detail = "cost=N;parts=partA,partB"`. The techId is sufficient to unlock.

**Guard interaction**: `TechResearchPatch` blocks re-research of committed-but-unreplayed techs via `MilestoneStore.GetCommittedTechIds()`. Shows `CommittedActionDialog` with science cost info. After replay, the tech IS unlocked, so the player cannot research it again (KSP's own UI grays out researched nodes). The patch becomes a safety net rather than the primary guard.

### Phase 2: Part Purchase Replay

**Why second**: Part purchases are closely related to tech unlocks (unlocking tech doesn't auto-purchase parts in some game modes) and the pattern is similar.

**KSP API**: `ResearchAndDevelopment.Instance.AddExperimentalPart(AvailablePart)` or `PartLoader.getPartInfoByName(partName)` + mark as purchased. Need to investigate during planning.

**Data available**: `PartPurchased` events have `key = partName` and `detail = "cost=N"`.

**Guard interaction**: No `PartPurchasePatch` exists currently. After replay, the part IS purchased, so KSP's own UI won't offer it for purchase again.

### Phase 3: Facility Upgrade Replay

**Why third**: Facility upgrades are less common but have the same pattern.

**KSP API**: `UpgradeableFacility.SetLevel(int)` - the same method that `FacilityUpgradePatch` guards. The replay must bypass the patch (or the patch must recognize Parsek-initiated upgrades).

**Data available**: `FacilityUpgraded` events have `key = facilityId` and `valueBefore/valueAfter` (normalized levels 0.0-1.0). Need to convert normalized level to integer level for SetLevel.

**Guard interaction**: `FacilityUpgradePatch` blocks upgrades of committed facilities via `MilestoneStore.GetCommittedFacilityUpgrades()`. Shows `CommittedActionDialog`. During replay, we need to either:
- Set a `SuppressFacilityPatch` flag (like `SuppressResourceEvents`)
- Or call the underlying upgrade method directly, bypassing the patch

### Phase 4: Crew Hire Replay

**Why last**: Crew hiring is the least impactful and interacts with the existing crew reservation system.

**KSP API**: `HighLogic.CurrentGame.CrewRoster.AddCrewMember()` or hire via KSP's crew management API. Need to investigate during planning.

**Data available**: `CrewHired` events have `key = kerbalName` and `detail = "trait=Pilot"`.

**Guard interaction**: No `CrewHirePatch` exists. After replay, the kerbal IS in the roster. The crew reservation system (`ParsekScenario.crewReplacements`, `ReserveCrewIn`, `ClearReplacements`) may need to handle the case where a reserved kerbal was hired during a committed recording and needs to be re-hired on replay.

**Complexity**: Crew names may collide (KSP generates random names). The recorded kerbal name might not match what KSP generates on re-hire. May need to use the `CrewHired` event data to create a kerbal with the exact recorded name and trait. This phase has the most unknowns.

## Behavior

### Trigger Points

Action replay is triggered in **two paths**:

1. **`ParsekScenario.ApplyBudgetDeductionWhenReady()`** - called on revert and go-back. After resource deduction but before marking milestones as fully replayed.

2. **`ParsekScenario.ApplyRewindResourceAdjustment()`** - called after a recording-based rewind (new system replacing restore points). Uses `ResourceBudget.ComputeTotalFullCost()` for differential adjustment. Currently does NOT mark milestones - only marks recordings and trees via `RecordingStore.MarkAllFullyApplied()`. Needs milestone marking + action replay added.

#### Current flow (ApplyBudgetDeductionWhenReady):
```
ApplyBudgetDeductionWhenReady():
  1. Wait for singletons (Funding, R&D, Reputation) - max 120 frames
  2. Guard: skip if budgetDeductionEpoch >= CurrentEpoch
  3. Compute reserved budget via ResourceBudget.ComputeTotal()
  4. Set SuppressResourceEvents = true
  5. Deduct from game funds/science/reputation
  6. Unset SuppressResourceEvents
  7. Mark recordings as fully applied (LastAppliedResourceIndex = Points.Count - 1)
  8. Mark trees as fully applied (ResourcesApplied = true)
  9. Mark milestones as fully replayed (LastReplayedEventIndex = Events.Count - 1)
```

#### New flow:
```
ApplyBudgetDeductionWhenReady():
  1-6. (unchanged - compute, suppress, deduct)
  7. Replay committed actions from unreplayed milestones (NEW)
  8. Mark recordings as fully applied
  9. Mark trees as fully applied
  10. Mark milestones as fully replayed
```

#### Current flow (ApplyRewindResourceAdjustment):
```
ApplyRewindResourceAdjustment():
  1. Capture RewindReserved snapshot
  2. Wait for singletons - max 120 frames
  3. Compute current full cost via ComputeTotalFullCost()
  4. Calculate differential delta
  5. Apply with SuppressResourceEvents
  6. MarkAllFullyApplied() - recordings + trees only
  7. Set budgetDeductionEpoch = CurrentEpoch
```

#### New flow:
```
ApplyRewindResourceAdjustment():
  1-5. (unchanged)
  6. Replay committed actions from unreplayed milestones (NEW)
  7. MarkAllFullyApplied() - recordings + trees
  8. Mark milestones as fully replayed (NEW - currently missing)
  9. Set budgetDeductionEpoch = CurrentEpoch
```

### Action Replay Logic

Extract a shared `ReplayCommittedActions()` method called from both trigger points:

```csharp
internal static void ReplayCommittedActions(List<Milestone> milestones)
{
    for each milestone where Committed && LastReplayedEventIndex + 1 < Events.Count:
        for each event from LastReplayedEventIndex+1 to Events.Count-1:
            switch event.eventType:
                TechResearched   → unlock tech node (Phase 1)
                PartPurchased    → purchase part (Phase 2)
                FacilityUpgraded → upgrade facility (Phase 3)
                CrewHired        → hire crew member (Phase 4)
                other            → skip (contracts, crew status, etc.)
}
```

Each action type has its own replay method with error handling and logging. Failures are logged but do not block the overall flow - partial replay is better than no replay.

### Suppression During Replay

Action replay will trigger KSP's own game events (`OnTechnologyResearched`, `OnPartPurchased`, `onKerbalAdded`, `OnFundsChanged` for facility upgrades). These must NOT be re-recorded as new game state events.

Current suppression flags:
- `SuppressResourceEvents` - used during budget deduction and timeline replay (checked by `OnFundsChanged`, `OnScienceChanged`, `OnReputationChanged`, `OnScienceReceived`)
- `SuppressCrewEvents` - used during crew reservation mutations (checked by `OnKerbalAdded`, `OnKerbalRemoved`, `OnKerbalStatusChange`)

**Note**: `OnTechResearched` and `OnPartPurchased` have **NO suppression checks** currently. They always record.

**Approach**: Add a single `SuppressActionReplay` flag to `GameStateRecorder`, checked in:
- `OnTechResearched` - currently unguarded
- `OnPartPurchased` - currently unguarded
- `OnKerbalAdded` - already guarded by `SuppressCrewEvents`, but `SuppressActionReplay` provides belt-and-suspenders
- Facility change handlers - already guarded by `SuppressResourceEvents` (facility costs arrive as `OnFundsChanged`)

Set `SuppressActionReplay = true` around the entire `ReplayCommittedActions()` call, alongside `SuppressResourceEvents = true` (which is already set in both trigger paths).

## Edge Cases

1. **Tech node already researched (loaded from save)**
   - Check `ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available` before unlocking.
   - If already available, skip. Log: "Tech '{techId}' already researched - skipping replay."

2. **Part already purchased**
   - Check if part is already in the player's inventory before purchasing.
   - If already purchased, skip. Log: "Part '{partName}' already purchased - skipping replay."

3. **Facility already at target level**
   - Check current level before upgrading.
   - If already at or above target level, skip.

4. **Tech node requires prerequisite that isn't researched**
   - Can happen if milestones are from different epochs and prerequisite was in an abandoned branch.
   - Attempt unlock anyway (KSP may silently ignore). Log warning.
   - v1 acceptable limitation.

5. **Part name uses underscore (KSP converts to dot)**
   - Use dot-form for `PartLoader.getPartInfoByName()` (known gotcha from MEMORY.md).

6. **Crew member name collision**
   - If a kerbal with the same name already exists in the roster, skip.
   - v1 acceptable limitation - the common case is that the kerbal was hired fresh.

7. **Facility upgrade cost not refunded on revert**
   - `ComputeFacilityUpgradeCost` returns 0 (facility costs captured in FundsChanged events, excluded from milestones).
   - Facility upgrade costs are already baked into recording trajectory fund deltas.
   - No double-deduction risk.

8. **Multiple tech unlocks in dependency order**
   - Events are stored in UT order within a milestone. If basicRocketry → generalRocketry were both researched, they appear in order.
   - Replay in event order ensures prerequisites are met.

9. **Actions from abandoned epoch**
   - Only replay actions from milestones in `CurrentEpoch`.
   - `MilestoneStore.GetCommittedTechIds()` and `GetCommittedFacilityUpgrades()` already filter to committed milestones with unreplayed events.
   - Milestones from abandoned epochs are ignored.

10. **Go-back vs. revert vs. rewind**
    - Revert and go-back funnel through `ApplyBudgetDeductionWhenReady`.
    - Rewind uses `ApplyRewindResourceAdjustment` (separate path).
    - Action replay works identically in both - shared `ReplayCommittedActions()` method.

11. **Wipe interaction**
    - "Wipe All Recordings" (Settings window) clears committed recordings, unreserves crew, destroys ghosts, clears science subjects. Milestones still exist but have no associated recordings - action replay skips milestones with no unreplayed events.
    - "Wipe All Game Actions" (Settings window) clears all milestones via `MilestoneStore.ClearAll()` and invalidates `ResourceBudget`. No milestones → no actions to replay.
    - Either wipe path is safe. No special handling needed.

12. **Contracts**
    - Contract completion is event-driven (orbit, land, etc.), not a discrete action.
    - Contract acceptance could theoretically be replayed, but:
      - The original contract may not exist after revert (KSP generates contracts dynamically)
      - Replaying acceptance without the contract existing would fail
      - Contract advance funds are captured in trajectory deltas
    - **Not replayed in any phase.** Acceptable v1 limitation - contracts are dynamic and cannot be reliably reproduced.

13. **FacilityUpgradePatch blocks Parsek-initiated upgrades**
    - During action replay, `UpgradeableFacility.SetLevel(int)` will be intercepted by `FacilityUpgradePatch`.
    - The patch checks `MilestoneStore.GetCommittedFacilityUpgrades()` - which includes the facility we're trying to replay.
    - Solution: add a `SuppressFacilityPatch` flag checked at the top of `FacilityUpgradePatch.Prefix()`. Set true during replay.
    - Alternative: replay before the milestone's `LastReplayedEventIndex` advances - but this is fragile. Flag is cleaner.

14. **TechResearchPatch blocks Parsek-initiated unlocks**
    - Same issue as #13 - `TechResearchPatch.Prefix()` blocks techs in `GetCommittedTechIds()`.
    - Solution: same pattern - add a `SuppressTechPatch` flag. Or a single `SuppressBlockingPatches` flag used by both.
    - Since we're replaying before `LastReplayedEventIndex` advances, the patches WILL see these techs/facilities as committed-but-unreplayed and block them.
    - **A single `SuppressBlockingPatches` flag is the cleanest approach**, checked at the top of both `TechResearchPatch.Prefix()` and `FacilityUpgradePatch.Prefix()`.

## What Doesn't Change

- Recording format - no new fields
- Ghost visual building - unchanged
- Resource budget computation - unchanged (action replay doesn't change costs, only performs the actions)
- Science subject tracking - unchanged (ScienceSubjectPatch + committed/original science dicts handle science separately)
- Milestone serialization - unchanged (`LastReplayedEventIndex` already persisted)
- Existing Harmony patches - remain as safety nets (with new bypass flag for replay)
- CommittedActionDialog - unchanged (only shown when patches block, not during replay)

## Backward Compatibility

- No serialization changes - no version bump needed
- Existing saves work unchanged - action replay simply starts working for committed milestones
- No new persistent data - replay is purely runtime behavior triggered by existing milestone data

## Diagnostic Logging

### Tech replay
- `[Parsek][ActionReplay] Replaying {count} unreplayed actions from {milestoneCount} milestones`
- `[Parsek][ActionReplay] Tech unlock: '{techId}' - success`
- `[Parsek][ActionReplay] Tech unlock: '{techId}' - already researched, skipping`
- `[Parsek][ActionReplay] Tech unlock: '{techId}' - FAILED: {error}`

### Part replay
- `[Parsek][ActionReplay] Part purchase: '{partName}' - success`
- `[Parsek][ActionReplay] Part purchase: '{partName}' - already purchased, skipping`
- `[Parsek][ActionReplay] Part purchase: '{partName}' - part not found (PartLoader), skipping`

### Facility replay
- `[Parsek][ActionReplay] Facility upgrade: '{facilityId}' level {from} → {to} - success`
- `[Parsek][ActionReplay] Facility upgrade: '{facilityId}' - already at level {current}, skipping`
- `[Parsek][ActionReplay] Facility upgrade: '{facilityId}' - facility not found, skipping`

### Crew replay
- `[Parsek][ActionReplay] Crew hire: '{kerbalName}' trait={trait} - success`
- `[Parsek][ActionReplay] Crew hire: '{kerbalName}' - already in roster, skipping`
- `[Parsek][ActionReplay] Crew hire: '{kerbalName}' - FAILED: {error}`

### Summary
- `[Parsek][ActionReplay] Replay complete: {techCount} tech, {partCount} parts, {facilityCount} facilities, {crewCount} crew ({skipCount} skipped, {failCount} failed)`

## Test Plan

### Unit tests

1. **ReplayTechUnlock - skips already-researched tech**
   - Given: milestone with TechResearched event for "basicRocketry", tech already available in R&D.
   - Assert: no unlock attempted, log contains "already researched, skipping".
   - Guards against: duplicate tech unlock attempts causing errors.

2. **ReplayPartPurchase - skips already-purchased part**
   - Given: milestone with PartPurchased event for "mk1pod.v2", part already purchased.
   - Assert: no purchase attempted, log contains "already purchased, skipping".
   - Guards against: duplicate part purchase.

3. **ReplayFacilityUpgrade - skips facility at target level**
   - Given: milestone with FacilityUpgraded event for LaunchPad to level 2, facility already at level 2.
   - Assert: no upgrade attempted, log contains "already at level".
   - Guards against: duplicate facility upgrade.

4. **SuppressActionReplay flag prevents re-recording**
   - Given: SuppressActionReplay = true, fire OnTechResearched event.
   - Assert: no GameStateEvent added to store.
   - Guards against: action replay causing recursive event recording.

5. **Replay order matches milestone event order**
   - Given: milestone with events [TechA, PartB, TechC] in order.
   - Assert: replay processes in order TechA → PartB → TechC.
   - Guards against: dependency ordering issues (TechC may depend on TechA).

6. **Replay only processes unreplayed events**
   - Given: milestone with 5 events, LastReplayedEventIndex = 2.
   - Assert: only events [3] and [4] are processed.
   - Guards against: re-replaying already-applied actions.

7. **Replay skips non-action events**
   - Given: milestone with ContractAccepted, FundsChanged, TechResearched events.
   - Assert: only TechResearched processed, others skipped.
   - Guards against: attempting to replay non-replayable event types.

8. **Replay handles empty milestone gracefully**
   - Given: milestone with 0 events.
   - Assert: no errors, no actions taken.
   - Guards against: index-out-of-range on empty event list.

9. **SuppressBlockingPatches bypasses TechResearchPatch**
   - Given: SuppressBlockingPatches = true, tech in committed set.
   - Assert: TechResearchPatch.Prefix returns true (allows through).
   - Guards against: replay blocked by own patches.

10. **SuppressBlockingPatches bypasses FacilityUpgradePatch**
    - Given: SuppressBlockingPatches = true, facility in committed set.
    - Assert: FacilityUpgradePatch.Prefix returns true (allows through).
    - Guards against: replay blocked by own patches.

### Integration tests

11. **Full replay flow with mixed actions**
    - Given: 2 milestones, first with [TechResearched, PartPurchased], second with [FacilityUpgraded].
    - Simulate: call ReplayCommittedActions with these milestones.
    - Assert: summary log shows correct counts.
    - Guards against: cross-milestone replay issues.

12. **Replay idempotency**
    - Call ReplayCommittedActions twice with the same milestones.
    - Assert: second call skips all actions (already done).
    - Guards against: non-idempotent replay causing errors.

### Edge case tests

13. **Part name with underscore → dot conversion**
    - Given: PartPurchased event with key "solidBooster_v2".
    - Assert: lookup uses "solidBooster.v2" (dot form).
    - Guards against: KSP part name conversion gotcha.

14. **Crew hire with existing name**
    - Given: CrewHired event for "Jeb Kerman", Jeb already in roster.
    - Assert: skip, no error.
    - Guards against: duplicate crew causing roster corruption.

15. **Replay summary logged**
    - Trigger replay with 2 tech + 1 part + 1 skip.
    - Assert log contains: "Replay complete: 2 tech, 1 parts, 0 facilities, 0 crew (1 skipped, 0 failed)".
    - Guards against: silent replay without diagnostic output.
