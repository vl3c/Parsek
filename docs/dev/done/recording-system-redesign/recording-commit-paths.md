# Recording Commit Paths

All paths through which a recording can be committed to the timeline, and what happens to the vessel in each case.

Every path funnels through `RecordingStore.CommitPending()`, which adds the recording to `CommittedRecordings`, captures a game state baseline, and creates a milestone.

## Vessel persistence rule

A vessel spawns at EndUT only when `VesselSnapshot != null && !VesselSpawned`. Out of 14 distinct outcomes, only 3 result in vessel persistence (#1, #4, #8).

## Commit paths

| # | Trigger | User Choice | Vessel Spawns? | What Happens |
|---|---------|-------------|----------------|--------------|
| 1 | Revert (standalone) | Merge to Timeline | **Yes** (deferred at EndUT) | Snapshot kept, crew reserved+swapped |
| 2 | Revert (standalone) | Discard | **No** | Nothing committed, crew unreserved |
| 3 | Revert (standalone, vessel destroyed) | Merge to Timeline | **No** | Snapshot nulled, ghost only |
| 4 | Revert (chain) | Merge to Timeline | **Yes** (final segment only) | Final snapshot kept, crew reserved+swapped |
| 5 | Revert (chain) | Discard All | **No** | All chain siblings + pending removed |
| 6 | Revert (chain, final vessel destroyed/EVA) | Merge to Timeline | **No** | All snapshots nulled, ghost only |
| 7 | Revert (chain, final vessel destroyed/EVA) | Discard All | **No** | All chain siblings + pending removed |
| 8 | Commit Flight button | - (no dialog) | **Yes** (already active) | `VesselSpawned=true`, no duplicate spawn |
| 9 | Leave Flight without revert | - (auto) | **No** | Snapshot nulled, crew unreserved |
| 10 | EVA exit mid-recording | - (auto chain) | **No** | Parent segment ghost-only |
| 11 | EVA boarding mid-recording | - (auto chain) | **No** | EVA segment ghost-only; parent snapshot nulled on boarding back |
| 12 | Docking mid-recording | - (auto chain) | **No** | Snapshot nulled explicitly |
| 13 | Undocking mid-recording | - (auto chain) | **No** | Snapshot nulled explicitly |
| 14 | Atmosphere boundary / SOI change | - (auto chain) | **No** | Snapshot nulled explicitly |

## No recovery option in merge dialog

Parsek does not handle vessel recovery. After merge, all surviving vessels spawn at their final positions. The player uses KSP's native recovery tools:

- **Tracking Station**: right-click any vessel → "Recover Vessel" (works for any situation, any body)
- **Flight scene**: green "Recover" button (landed/splashed active vessels only)
- **Funds**: KSP calculates recovery value automatically based on distance from KSC

This keeps the merge dialog to two options: **Merge to Timeline** and **Discard**.

## Details by category

### User-triggered with merge dialog (revert)

Triggered by `ParsekFlight.OnSceneChangeRequested` when leaving the Flight scene via revert. Recording is stashed as pending, then `MergeDialog.Show()` presents two options:

**Standalone dialog** (`MergeDialog.ShowStandaloneDialog`): shown when the pending recording has no chain.
- "Merge to Timeline" - commit recording, spawn vessel at EndUT (if not destroyed)
- "Discard" - nothing committed

**Chain dialog** (`MergeDialog.ShowChainDialog`): shown when the pending recording belongs to a chain (EVA/dock/boundary segments). Mid-chain siblings are already committed as ghost-only. Only the final segment is pending.
- "Merge to Timeline" - commit final segment, spawn vessel at EndUT (if not destroyed)
- "Discard All" - remove all chain siblings from committed recordings plus discard pending segment

### User-triggered without dialog (Commit Flight)

`ParsekFlight.CommitFlight()` - triggered by the "Commit Flight" UI button. Stops recording, snapshots the vessel, and commits immediately. The vessel remains active in-game (`VesselSpawned=true` prevents duplicate spawn at EndUT). Blocked during active chains.

### Automatic: scene change without revert

`ParsekScenario.Update()` detects a pending recording outside the Flight scene (Abort Mission, Return to Space Center, etc.). Auto-commits with `VesselSnapshot = null` - no vessel persistence, crew unreserved.

### Automatic: mid-chain segment commits

All mid-chain commits null `VesselSnapshot` before calling `CommitPending()`. The vessel continues to exist in-game as the active vessel; only the final segment (decided at revert) can persist it.

- **EVA exit** (`CommitChainSegment`): parent vessel segment committed. Snapshot survives initially for continuation sampling but is nulled if the EVA kerbal boards back.
- **EVA boarding** (`CommitChainSegment`): EVA segment committed as ghost-only.
- **Docking/Undocking** (`CommitDockUndockSegment`): segment committed with `VesselSnapshot = null`.
- **Atmosphere boundary / SOI change** (`CommitBoundarySplit`): segment committed with `VesselSnapshot = null`.

## Key code locations

- `MergeDialog.ShowStandaloneDialog` - `MergeDialog.cs:43`
- `MergeDialog.ShowChainDialog` - `MergeDialog.cs:136`
- `ParsekFlight.CommitFlight` - `ParsekFlight.cs:1573`
- `ParsekScenario.Update` (auto-commit) - `ParsekScenario.cs:427`
- `ParsekFlight.CommitChainSegment` - `ParsekFlight.cs:707`
- `ParsekFlight.CommitDockUndockSegment` - `ParsekFlight.cs:1078`
- `ParsekFlight.CommitBoundarySplit` - `ParsekFlight.cs:1151`
- `RecordingStore.CommitPending` - `RecordingStore.cs:221`
