# Fix: F5/F9 Recording Gap Bugs

**Branch:** `fix/f5f9-recording-gap`
**Bugs:** #293 (race in OnFlightReady), #294 (standalone quickload-resume missing)
**Based on:** PR #184 (reentrancy guard for restore coroutine)

---

## Problem Summary

Two F5/F9 bugs cause recording gaps — the player flies for minutes/hours with no recorder active:

1. **Bug #293: Race in OnFlightReady** — After F9 quickload with an active tree, the restore coroutine (`RestoreActiveTreeFromPending`) starts and yields waiting for the vessel to load. But the fallback pending tree check at `ParsekFlight.cs:4211` runs synchronously in the same frame, sees `HasPendingTree` is still true (coroutine hasn't popped it yet), and fires `MergeDialog.ShowTreeDialog`. With autoMerge ON, the tree is committed immediately. The coroutine eventually resumes but the tree is gone — no recording restarts. Observed in `logs/2026-04-10_engine-plume-bug/KSP.log` line 10604: 28 minutes of orbital flight (UT 695→2376) not recorded.

2. **Bug #294: Standalone quickload-resume missing** — Tree mode has a full Limbo stash/restore mechanism (`PARSEK_ACTIVE_TREE` node + `PendingTreeState.Limbo` + `RestoreActiveTreeFromPending` coroutine). Standalone mode has nothing equivalent. When F5 happens during a standalone recording, F9 triggers `DiscardStashedOnQuickload` which blindly discards the pending recording without distinguishing "started before F5" (should resume) from "started after F5" (should discard). The in-progress buffer is also never serialized to `.sfs` during OnSave (tree mode has `FlushRecorderIntoActiveTreeForSerialization`, standalone has no equivalent).

---

## Fix Design

### Fix A: Guard the fallback pending tree check (Bug #293)

**File:** `ParsekFlight.cs:4211`

**Change:** Add `&& !restoringActiveTree` to the existing `HasPendingTree` check:

```csharp
// Before:
if (RecordingStore.HasPendingTree)

// After:
if (RecordingStore.HasPendingTree && !restoringActiveTree)
```

**Rationale:** PR #184 added the `restoringActiveTree` static flag and guards at `OnFlightReady` entry (line 4113), `FinalizeTreeOnSceneChange`, `OnVesselWillDestroy`, and `OnVesselSwitchComplete`. But it missed the intra-method fallback at line 4211. The coroutine sets `restoringActiveTree = true` before its first yield, so by the time the fallback check runs, the guard is already set. One-line fix.

Also guard the pending standalone check at line 4221 for consistency:
```csharp
if (RecordingStore.HasPending && !restoringActiveTree)
```

**Logging:** Change the existing Warn to include the guard skip:
```csharp
ParsekLog.Info("Flight",
    "OnFlightReady: pending tree '{pt.TreeName}' skipped — restore coroutine in progress");
```

### Fix B: Standalone quickload-resume (Bug #294)

This is the bigger fix. Three sub-parts:

#### B1: Serialize active standalone recording on F5

**File:** `ParsekScenario.cs` — `OnSave` method

**Change:** After `SaveActiveTreeIfAny(node)` (which handles tree mode), add a parallel path for standalone mode. When in FLIGHT scene with an active standalone recorder (no activeTree), serialize the in-progress recording into a `PARSEK_ACTIVE_STANDALONE` ConfigNode:

```csharp
// In OnSave, after SaveActiveTreeIfAny:
SaveActiveStandaloneIfAny(node);
```

New method `SaveActiveStandaloneIfAny`:
- Guard: `HighLogic.LoadedScene != FLIGHT` → return
- Guard: `ParsekFlight.Instance?.ActiveTreeForSerialization != null` → return (tree mode, not standalone)
- Guard: `ParsekFlight.Instance?.ActiveRecorderForSerialization == null || !IsRecording` → return
- Flush the recorder buffer into a temporary Recording object
- Serialize it as a `PARSEK_ACTIVE_STANDALONE` ConfigNode with:
  - `vesselName` — vessel name at recording time
  - `vesselPid` — vessel persistent ID for matching on reload
  - `recordingId` — preserve the recording ID across F5/F9
  - `rewindSave` — the recorder's RewindSaveFileName
  - Inline POINT/PART_EVENT/ORBIT_SEGMENT/TRACK_SECTION/FLAG_EVENT nodes
- Also write sidecar files (`.prec` etc.) so the trajectory data survives

**Key insight:** Unlike tree mode where `FlushRecorderIntoActiveTreeForSerialization` clears the recorder's buffers (because the tree recording object persists), the standalone path must NOT clear the buffers — the recorder keeps running after F5. Instead, snapshot the buffer contents into the ConfigNode without clearing them.

#### B2: Restore standalone recording on F9

**File:** `ParsekScenario.cs` — `OnLoad` method

**Change:** In the `OnLoad` method, after loading committed recordings and trees, check for a `PARSEK_ACTIVE_STANDALONE` node:

```csharp
// In OnLoad, after tree restore logic:
TryRestoreActiveStandaloneNode(node);
```

New method `TryRestoreActiveStandaloneNode`:
- Extract `PARSEK_ACTIVE_STANDALONE` node from the loaded ConfigNode
- If absent → return (no active standalone was saved)
- Store the data in a new static field `pendingActiveStandaloneData` (ConfigNode + metadata)
- Set a new flag `ScheduleActiveStandaloneRestoreOnFlightReady = true`

**File:** `ParsekFlight.cs` — `OnFlightReady` method

After the tree restore dispatch (line 4141-4154), add standalone restore:

```csharp
// After tree restore dispatch:
if (ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady
    && !restoringActiveTree)
{
    ParsekScenario.ScheduleActiveStandaloneRestoreOnFlightReady = false;
    StartCoroutine(RestoreActiveStandaloneFromPending());
}
```

New coroutine `RestoreActiveStandaloneFromPending`:
- Wait up to 3s for `FlightGlobals.ActiveVessel` to match by name or PID
- If matched: create a new `FlightRecorder`, seed it with the saved data (points, events, etc.), call `StartRecording(isPromotion: true)` so it resumes appending
- If not matched: discard the saved data, log a warning

#### B3: Don't discard pre-F5 standalone recordings on quickload

**File:** `ParsekScenario.cs` — `DiscardStashedOnQuickload`

**Change:** The current code at line 169 unconditionally discards any pending standalone recording stashed this transition. With B1/B2 in place, the standalone recording will be restored from the `PARSEK_ACTIVE_STANDALONE` node, so the stashed version (which has post-F5 data that should be discarded) is correctly discarded. No change needed here — the stash contains future-timeline data and should be discarded. The `PARSEK_ACTIVE_STANDALONE` node from the quicksave contains the F5-point data that will be restored.

**This is actually correct as-is.** The stash at `StashPendingOnSceneChange` captures the recording at F9 time (with post-F5 data). Discarding it is right. The restore comes from the quicksave's `PARSEK_ACTIVE_STANDALONE` node, not from the stash.

---

## Scope Control

### In scope
- Fix A: One-line guard on fallback pending tree/standalone checks
- Fix B: Standalone quickload-resume (serialize on F5, restore on F9)
- Unit tests for new pure/static methods
- Log-assertion tests
- CHANGELOG + todo updates

### Out of scope
- Standalone recording `restoringActiveStandalone` reentrancy guard (not needed — standalone restore is simpler than tree restore with no BackgroundRecorder or BackgroundMap to manage)
- Standalone F5/F9 mode parity with ALL tree-mode edge cases (EVA parent fallback, PID remap, etc.) — standalone mode is simpler (single vessel, no branches)
- Bug #290 broader F5/F9 verification — this PR fixes the specific observed bugs, broader verification is future work

---

## File Changes

| File | Change |
|------|--------|
| `ParsekFlight.cs` | Fix A: guard at line 4211/4221. Fix B: `RestoreActiveStandaloneFromPending` coroutine, standalone restore dispatch in `OnFlightReady` |
| `ParsekScenario.cs` | Fix B: `SaveActiveStandaloneIfAny`, `TryRestoreActiveStandaloneNode`, `ScheduleActiveStandaloneRestoreOnFlightReady` flag, `pendingActiveStandaloneData` |
| `RecordingStore.cs` | Fix B: `PendingActiveStandaloneNode` storage (ConfigNode + metadata struct) |
| `Source/Parsek.Tests/QuickloadStandaloneTests.cs` | New: unit tests for `SaveActiveStandaloneIfAny` serialization, `TryRestoreActiveStandaloneNode` deserialization, guard conditions |
| `CHANGELOG.md` | New entries for #293 and #294 |
| `docs/dev/todo-and-known-bugs.md` | New bug entries #293 and #294, mark as fixed |

---

## Test Plan

### Unit tests (QuickloadStandaloneTests.cs)
1. `FallbackPendingTreeCheck_SkippedWhenRestoringActiveTree` — set `restoringActiveTree = true`, verify fallback check would skip
2. `SaveActiveStandaloneIfAny_TreeMode_Skips` — verify no `PARSEK_ACTIVE_STANDALONE` node when tree is active
3. `SaveActiveStandaloneIfAny_NoRecorder_Skips` — verify no node when recorder is null
4. `SaveActiveStandaloneIfAny_ActiveRecorder_SerializesData` — verify ConfigNode contains points, events, vesselName, vesselPid, recordingId
5. `TryRestoreActiveStandaloneNode_NoNode_ReturnsFalse` — verify no-op when node absent
6. `TryRestoreActiveStandaloneNode_ValidNode_SetsFlag` — verify ScheduleActiveStandaloneRestoreOnFlightReady is set
7. `DiscardStashedOnQuickload_WithActiveStandalone_DiscardsPending` — verify stashed recording still discarded (correct behavior — restore comes from saved node, not stash)

### In-game tests (deferred — requires live KSP)
- `Quickload_MidStandaloneRecording_ResumesRecording` — F5 during standalone recording, F9, verify recording resumed with same ID and continued point count
- `QuickloadTree_FallbackGuard_NoAutoMerge` — F5 during tree recording, F9, verify restore coroutine runs instead of fallback merge dialog

---

## Implementation Order

1. Fix A (one-line guard) — immediate, no dependencies
2. Fix B1 (serialize standalone on F5) — `SaveActiveStandaloneIfAny`
3. Fix B2 (restore standalone on F9) — `TryRestoreActiveStandaloneNode` + `RestoreActiveStandaloneFromPending`
4. Tests
5. Documentation (CHANGELOG, todo)
6. Build + verify tests pass
