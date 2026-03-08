# Going Back in Time: Design

## The Feature

The player can go back to any earlier point in time and launch new missions while existing recordings (from the "future") play out as ghosts alongside them. This is a core feature of Parsek.

## The Git Analogy

Parsek's timeline works like a git repository:

| Git | Parsek |
|-----|--------|
| main branch | The single timeline |
| Commits | Committed recordings (immutable) |
| Working directory | Current game session |
| `git checkout <commit>` | Go back to an earlier UT |
| `git commit` | Commit a recording |
| Merge | Adding a recording to the timeline |

In git, when you checkout an old commit and make new commits, the old history doesn't change. When you merge back, conflicts are resolved. The result is always a single linear history. Parsek works the same way — recordings are immutable commits that coexist on one timeline.

## Why There Are No Merge Conflicts

Every recording commit is effectively a **fast-forward merge** because recordings are additive and independent:

- **Crew**: The reservation system prevents double-booking. If Jeb is in a recording, he's reserved. The player gets a replacement. Already implemented.
- **Resources**: Resources are globally budgeted across all recordings. If future recordings have claimed funds, those funds are unavailable now, even if the player is at an earlier UT. The player sees: "you can't launch this mission — the funds are committed to future recordings." This is like git: you can't delete a file that another branch depends on without resolving the dependency.
- **Vessels**: Proximity offset prevents spawn collisions. Already implemented.
- **Tech/parts**: A recording captures the vessel as-built. The ghost replays with whatever parts it had regardless of current tech level. Non-issue.
- **Facilities**: A recording doesn't depend on facility level. The vessel was already built and recorded.

## Resource Budgeting

The key insight: resources (funds, science, reputation) belong to the timeline, not to the current UT. Committed recordings have already "spent" or "earned" their resource deltas. When the player goes back in time, those commitments don't disappear.

**Example:**
1. Player at UT 10000 has 50,000 funds
2. Player records a mission from UT 10000-20000 that costs 15,000 to launch. Commits it.
3. Player goes back to UT 10000
4. Available funds = 50,000 minus 15,000 already committed = 35,000 available
5. Player tries to launch a 40,000-fund mission → blocked: "insufficient funds (35,000 available, 15,000 committed to future recordings)"
6. Player launches a 20,000-fund mission instead → allowed, 15,000 remaining

This is simple accounting. No paradoxes. The player always sees what they can actually spend.

Resource deltas earned BY recordings (science from experiments, contract rewards) are applied at the correct UT during ghost playback, just as they are today.

## How "Going Back" Works Mechanically

1. **Restore points**: Parsek auto-saves at commit points (piggybacking on KSP's quicksave system or Parsek-specific snapshots). These are tagged with UT and serve as restore points.

2. **Go Back UI**: Player picks a restore point. Parsek loads the corresponding save state, then re-injects its recording data (all recordings, crew reservations, resource commitments). The game state (funds, tech, facilities) naturally reverts to that point via the save load.

3. **Resource adjustment**: After loading the restore point, adjust available resources to account for committed recording costs that occur after the restore point's UT. The player's "available budget" = save state resources minus committed future costs.

4. **LastAppliedResourceIndex reset**: For recordings whose UT range is after the restore point, reset `LastAppliedResourceIndex` so their resource deltas re-apply during ghost playback.

5. **Play forward**: Everything works as normal — ghosts appear at scheduled UTs, vessels spawn at EndUT, resource deltas apply as ghosts finish.

## What Game State Events / Baselines Are For

The game state event system is NOT for reversal. It serves as:

- **Audit log**: Track what happened in the career for display and debugging
- **Timeline visualization**: Show contracts, tech, facilities, and recordings together on a timeline view
- **Resource budgeting**: Know how much was spent/earned at each point to calculate available resources
- **Conflict detection UI**: Warn the player before they commit a recording that would overdraw resources

## What This Does NOT Require

- No timeline branching (one timeline, always)
- No state reversal from event logs (snapshot-based restore)
- No baseline restoration logic (baselines are for display, not rollback)
- No "undo" mechanism (recordings are immutable)

## Implementation Status

**Done (Phase 5 foundation):**
- Game state event recording (18 event types: contracts, tech, crew, facilities, resources)
- Milestones — independent of recordings, epoch-isolated, with `FlushPendingEvents` for non-recording events
- Resource budget computation — on-the-fly from recordings + milestones, partial-replay aware
- Epoch isolation — revert increments epoch, old-branch events excluded
- Resource deduction on revert — committed costs deducted from KSP game state
- Action blocking — Harmony patches on `RDTech.UnlockTech` and `UpgradeableFacility.SetLevel`
- UI resource budget display — red/yellow over-commitment warnings
- Game state baselines — captured at commit points for future timeline visualization

**Done:**
- Per-recording rewind saves — quicksave captured at recording start, stored in `Parsek/Saves/`, owned by the recording
- Rewind UI — per-recording "Rewind" button in Recordings window, confirmation dialog, loads quicksave into Space Center

## Resolved Design Questions

- **UI**: Rewind button per recording in the Recordings window (not a separate picker dialog). Each recording with a rewind save shows the button.
- **Manual vs auto**: Auto-only. Quicksave captured at recording start for every fresh (non-promotion) recording.
- **Facility upgrades**: Just works — save load naturally reverts facilities, recordings don't depend on facility state.
- **Vessel spawning on rewind**: Vessels stripped from flight state, game loads into Space Center. Player can enter buildings or launch a new vessel, then watch recordings replay as ghosts.
