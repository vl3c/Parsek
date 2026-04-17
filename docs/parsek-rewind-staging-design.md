# Parsek - Rewind-to-Staging Design

*Design specification for Rewind Points on multi-controllable split events (staging, undocking) and the Unfinished Flights group that lets the player go back to a past split and control the sibling vessel they did not originally fly. Enables the "fly the booster back" gameplay: launch AB, stage, fly B to orbit, merge, then rewind to the staging moment and fly A down as a self-landing booster.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder and timeline systems with mid-mission rewind points anchored at controllable-split BranchPoints.*

**Version:** 0.1 (design draft)
**Status:** Proposed.
**Depends on:** `parsek-flight-recorder-design.md` (recording tree, BranchPoint, controller identity, ghost chains), `parsek-timeline-design.md` (rewind via quicksave, ledger replay, merge lifecycle), `parsek-game-actions-and-resources-recorder-design.md` (ledger replay semantics for career state).
**Out of scope:** Changes to ghost playback engine, merge dialog UI internals, orbital checkpointing, ledger recalculation engine. Debris splits (only one controllable child) continue to use existing behavior with no rewind point.

---

## 1. Problem

Today, when the player stages a vessel and both halves are controllable (e.g. AB stack splits into booster A and upper stage B, each with a probe core or command pod), Parsek continues recording whichever vessel the player stays focused on. The other vessel is background-recorded until it crashes, at which point its recording is considered terminal debris. If the player flies B to orbit and merges, A's BG-crash recording is the only record of what A did - and the player has no way to go back and actually fly A as a self-landing booster.

This design adds **Rewind Points** on every split that produces two or more controllable children. A Rewind Point is a durable object that (a) captures a KSP quicksave at the moment of the split and (b) retains the ProtoVessel snapshot of each controllable child. Any unfinished child appears in a new **Unfinished Flights** group; invoking its Rewind Point loads the quicksave, spawns the child live from its snapshot, and lets the player fly it. All other vessels (merged siblings, the pre-split parent trajectory) play as ghosts from the timeline. When the re-fly reaches a stable outcome and the player merges, the new recording replaces the old BG-crash entry on the timeline in place.

The feature is a natural extension of the existing undock snapshot path (ChainSegmentManager already calls `VesselSpawner.TryBackupSnapshot` on undock) and the existing rewind-to-launch quicksave path. Nothing new about the underlying physics or ledger - only a new user-facing entry point and the data plumbing to make split-moment snapshots first-class.

---

## 2. Terminology

- **Split event**: any BranchPoint of type `JointBreak` (decouple/stage) or `Undock` that produces one or more new vessels from a single parent.
- **Controllable child**: a vessel produced by a split event that has at least one `ModuleCommand` part (probe core or crewed pod). `IsTrackableVessel` already filters this.
- **Multi-controllable split**: a split event with two or more controllable children. Only these get a Rewind Point. Single-controllable splits (e.g. SRB shell drop without probe core) continue to behave exactly as today.
- **Rewind Point**: a durable record attached to a multi-controllable split BranchPoint. Holds the quicksave filename, the split UT, and references to the ProtoVessel snapshots of each controllable child. Survives save/quit/reload.
- **Finished flight**: a recording whose terminal state is either a stable outcome (landed, orbited, destroyed-by-player-intent, etc.) that has been explicitly merged by the player, OR a split event that hands control to children (the segment itself terminates at the split; its outcome is determined by its descendants).
- **Unfinished flight**: a controllable-child recording whose terminal state is an unmerged BG-crash (the player never flew it, or flew it and did not merge). Listed in the Unfinished Flights group. Rewind-eligible.
- **Unfinished Flights group**: a default recording subgroup in the Recordings Manager that collects every unmerged controllable-child recording. Membership is computed, not manually edited.
- **Re-fly session**: the gameplay session after invoking a Rewind Point. Starts when the quicksave loads and the live child vessel is spawned; ends on merge (becomes a finished flight) or on discard (tree reset or player explicit discard).
- **Replace-in-place**: when a re-fly session is merged, its new recording replaces the old BG-crash recording for the same vessel-identity under the same BranchPoint. The timeline entry UT and vessel identity are preserved; the trajectory and terminal state change.

---

## 3. Mental Model

### 3.1 Tree evolution across a full flight + re-fly cycle

Starting state: player launches vessel ABC and flies it.

```
[flight in progress, nothing committed yet]
ABC (recording, live-focused)
```

Player stages: ABC splits into AB + C. Both have command modules. Multi-controllable split -> quicksave taken, snapshots captured for AB and C. Player stays focused on C.

```
ABC (recording, terminates at split-1)
 |
 +-- AB   (BG-recorded, controllable)
 +-- C    (recording, live-focused)
     [RewindPoint-1 @ split-1, snapshots: {AB, C}]
```

Player flies C to orbit. C's BG recording for AB continues (AB is falling). AB burns up or crashes; its BG recording terminates at destruction.

```
ABC (recording, terminated at split-1)
 |
 +-- AB   (BG-crash, terminal at destruction UT)
 +-- C    (recording, live, terminal at orbit)
     [RewindPoint-1 @ split-1]
```

Player merges the session. ABC + C are immutable on the timeline. AB is unmerged -> Unfinished Flight. Rewind-Point-1 stays durable because AB is unmerged.

```
ABC          (immutable, ghost-playable)
 |
 +-- AB      (Unfinished Flight, BG-crash recording)
 +-- C       (immutable, ghost-playable, spawns at orbit)
     [RewindPoint-1 @ split-1, Unfinished Flights: {AB}]
```

Player opens Unfinished Flights, sees AB, clicks Rewind. Quicksave loads. UT resets to split-1. C becomes a ghost playing its orbit trajectory. ABC plays its pre-split trajectory as a ghost up to split-1 (instantaneously, because UT is already at split-1). AB spawns live from its snapshot. Player takes control of AB.

Player flies AB for a while, then stages: AB splits into A + B. Both have command modules. Multi-controllable split during re-fly -> new quicksave taken, snapshots captured for A and B. Player stays focused on B.

```
ABC          (immutable, ghost)
 |
 +-- AB      (re-fly in progress, terminates at split-2)
 |    |
 |    +-- A  (BG-recorded, controllable)
 |    +-- B  (recording, live-focused)
 |         [RewindPoint-2 @ split-2, snapshots: {A, B}]
 +-- C       (immutable, ghost)
     [RewindPoint-1 @ split-1]
```

Player lands B, merges. AB's re-fly replaces AB's old BG-crash recording in place (AB now terminates at split-2 instead of destruction). B is immutable. A is unmerged -> new Unfinished Flight.

```
ABC                      (immutable, ghost)
 |
 +-- AB                  (immutable, ghost, terminates at split-2)
 |    |
 |    +-- A              (Unfinished Flight, BG-crash)
 |    +-- B              (immutable, ghost, spawns at landing)
 |         [RewindPoint-2 @ split-2, Unfinished Flights: {A}]
 +-- C                   (immutable, ghost)
     [RewindPoint-1 @ split-1, Unfinished Flights: {} <- AB is now merged]
```

RewindPoint-1 is still durable only if any of its children are unmerged. Both AB and C are now merged, so RewindPoint-1's quicksave can be deleted (there is nobody to rewind back to; if the player wants to redo AB, they would have to revert to launch). RewindPoint-2 stays durable because A is unmerged.

Player can now Rewind A, fly it down, merge, and the Unfinished Flights group empties out.

### 3.2 Key invariants

1. **One Rewind Point per multi-controllable split, lifetime tied to any unmerged child.** RewindPoint lives as long as at least one of its child snapshots is still unmerged. When the last child merges, the RewindPoint and its quicksave file can be reaped.

2. **A segment terminates at either a stable outcome OR a split event.** AB's first life terminated at a BG crash (unfinished). After re-fly, AB's terminal is the split event that produced A and B (finished). The terminal type changed; the segment identity (AB) did not.

3. **Replace-in-place preserves tree identity.** Merging a re-fly of AB does not create a new node in the tree. It replaces AB's trajectory and terminal state. Every reference to AB (by other BranchPoints, Unfinished Flights lookups, ghost playback) continues to point to the same node.

4. **Only one vessel is live per session.** A Rewind Point may have multiple snapshots, but invoking it spawns exactly one child live (the one the player clicked). The rest of the snapshotted siblings are either already merged (play as ghosts) or still unmerged and remain as their existing BG-crash ghosts until the player invokes their own Rewind Points in separate sessions.

5. **Career state is replayed by the ledger, not preserved across quickload.** After loading the quicksave, career state (funds/science/reputation/contracts) drops to its split-1 value. As UT advances during the re-fly and ghost playback, the ledger re-applies committed career events from immutable recordings. The player sees numbers animate up. This is identical to rewind-to-launch semantics today.

6. **Unfinished Flights membership is computed.** The group is not a manual folder. Any recording whose terminal state is BG-crash (unmerged) AND whose parent BranchPoint is a multi-controllable split is in the group. Merge any such recording to remove it; discard the tree to purge all of them.

---

## 4. Data Model

### 4.1 RewindPoint

```
RewindPoint
    string  Id                      unique id (e.g. "rp_" + Guid)
    string  BranchPointId           BranchPoint this RewindPoint is anchored to
    double  UT                      snapshot UT (at or just after the joint break)
    string  QuicksaveFilename       filename under saves/<save>/Parsek/RewindPoints/
    List<string> ChildRecordingIds  controllable children captured at this split
    DateTime CreatedRealTime        wall-clock timestamp, for debugging
```

Children are referenced by recording id, not embedded. The ProtoVessel snapshot per child is already carried by the child recording's `VesselSnapshot` field (existing infrastructure today, see `Recording.cs` `VesselSnapshot` / `GhostVisualSnapshot`). This design reuses those fields exactly.

### 4.2 BranchPoint additions

Add one field to the existing `BranchPoint` struct:

```
string RewindPointId    null if split had 0 or 1 controllable children; non-null otherwise
```

This is the sole link from a BranchPoint to its RewindPoint. Existing BranchPoint ConfigNode serialization gets one new key (`REWIND_POINT_ID`, optional).

### 4.3 Recording.TerminalState additions

Add one computed property (not persisted):

```
bool IsUnfinishedFlight      => TerminalKind == BGCrash
                              && MergeState == NotMerged
                              && ParentBranchPoint?.RewindPointId != null
                              && ContainsControllableVessel
```

`MergeState` is the existing merge-marker on Recording (already persisted). `TerminalKind` is the existing classification (landed / orbited / crashed / etc.). The Unfinished Flights group enumerates `RecordingStore.CommittedRecordings.Where(r => r.IsUnfinishedFlight)`.

### 4.4 ParsekScenario persistence

New subsection under `PARSEK` scenario node:

```
REWIND_POINTS
    POINT
        id = rp_a1b2c3d4
        branchPointId = bp_x1y2z3
        ut = 1742538.43
        quicksaveFilename = rp_a1b2c3d4.sfs
        childRecordingId = rec_01234
        childRecordingId = rec_01235
        createdRealTime = 2026-04-17T21:35:12.000Z
    POINT
        ...
```

Quicksave files live in `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs`. They are produced by `GamePersistence.SaveGame(...)` at the moment the split is detected and the new vessels have been classified. Filenames are validated against the same path-traversal rules as recording ids (see `RecordingPaths.ValidateRecordingId`).

### 4.5 Unfinished Flights UI group

No new data. The group is a virtual node in the existing `GroupHierarchyStore` that computes its children at render time by scanning recordings for `IsUnfinishedFlight`. Members cannot be manually added or removed. If the player drags an Unfinished Flight into a regular group, the drag is rejected with a log line and a tooltip explaining that membership is automatic.

The group is always present but collapses when empty. Label format: `Unfinished Flights (N)` where N is the member count.

---

## 5. Behavior

### 5.1 At a multi-controllable split (recording-time)

Triggered from `ParsekFlight.DeferredJointBreakCheck` and `DeferredUndockBranch`, after the existing classification produces a `StructuralSplit` result with two or more newly-created controllable children.

Sequence:

1. For each controllable child, the existing `VesselSpawner.TryBackupSnapshot` already runs and produces a ConfigNode; this snapshot is stored on the child Recording's `VesselSnapshot` field (existing behavior, no change).
2. **New:** Create a `RewindPoint` object. Assign id, populate `BranchPointId`, `UT`, `ChildRecordingIds`.
3. **New:** Call `GamePersistence.SaveGame(<rewindPointId>, "persistent", SaveMode.OVERWRITE)` into `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs`. The save must happen on the same physics frame as the split classification, while all new vessels are stable and before any active flight physics has advanced further. KSP's save serialization is synchronous; this is feasible.
4. **New:** Write `RewindPointId` on the parent BranchPoint.
5. **New:** Add the RewindPoint to `ParsekScenario.RewindPoints`.
6. **New:** Log an `Info` line summarizing the event (see section 9).

If fewer than two children are controllable, skip steps 2-5 and continue today's behavior exactly. This preserves zero-overhead for single-controllable splits and debris.

### 5.2 Between split and merge (flight continues)

The player continues flying the focused vessel. Unfocused controllable children are BG-recorded by the existing background recorder. Those BG recordings carry a `TerminalKind` of `Crashed` (or similar) once destroyed, and a `MergeState` of `NotMerged` until the session merge dialog runs.

On session merge:

- The focused vessel's recording gets its final `TerminalKind` (Landed/Orbited/Crashed/Aborted per existing rules) and `MergeState = Merged`.
- Every BG-recorded controllable child at each multi-controllable split also has its `MergeState` transitioned to `Merged` (its BG-crash outcome is committed as a provisional terminal; it will show in the timeline).
- The Unfinished Flights group picks up every committed-but-terminal-as-BG-crash child and lists it.

RewindPoint lifetime: remains durable as long as at least one of its `ChildRecordingIds` still has `MergeState = Merged` AND `TerminalKind = BGCrash` AND `IsUnfinishedFlight == true`.

### 5.3 Invoking a Rewind Point

Player opens Recordings Manager, expands Unfinished Flights, selects an entry, clicks the Rewind button. This is a new button variant that appears only on Unfinished Flight rows - it reuses the rewind-to-launch confirmation dialog plumbing from `RecordingsTableUI.ShowRewindConfirmation` with a mid-mission variant message.

Confirmation dialog text (conceptual):
> Rewind to stage separation of <ParentVesselName> at UT <formatted>?
> <ChildVesselName> will spawn live at that moment. <N> previously-merged flights will play as ghosts. Career state will drop to the separation moment and re-advance as the timeline replays.
> Quicksave-backed rewind - your save file is already on disk. Cancel or Proceed.

On Proceed:

1. Resolve the RewindPoint: `branchPoint.RewindPointId` -> `RewindPoint` -> `QuicksaveFilename`.
2. Verify the quicksave file exists. If missing, abort with an error dialog and a `Warn` log; the RewindPoint is marked corrupted (see 6.11).
3. Invoke the existing rewind-to-launch path with the quicksave filename substituted. This re-uses `RewindContext.BeginRewind`, the load flow, scene transitions, ghost respawn, etc.
4. On scene entry post-load:
   - `Planetarium.UT` is at the split moment (restored by the save).
   - The quicksave's serialized vessels have been brought back, including pre-split state for vessels that no longer exist and post-split state for those that did.
   - Parsek's scenario state has been restored to what it was at save time - BUT the `RecordingStore` and `Ledger` in memory must reflect current committed state (B's orbit recording exists as immutable, etc.). See 5.4 for the reconciliation.
5. Spawn the clicked child live from its `VesselSnapshot` at the RewindPoint's UT. Assign it as the active vessel. Start a new recording on it (replacing its BG-crash recording in place when eventually merged).
6. All other vessels play ghost trajectories per existing ghost playback rules. Ghost chain tips, zone transitions, loop behavior all work unchanged.
7. Log an `Info` line for the rewind invocation.

### 5.4 Reconciliation: committed-timeline state across quickload

This is the subtle piece. The quicksave was taken at split-1. At that time, Parsek's scenario state had AB in progress as a single unterminated recording. Since then, the player has flown B to orbit and committed the session - AB's pre-split portion and C's orbit are now immutable timeline recordings. The in-memory `RecordingStore` reflects the current committed state.

When we load the quicksave, KSP restores its own serialized state (world, vessels, career, Parsek scenario from save time). Parsek scenario at save time did NOT yet contain C's orbit recording.

**Resolution:** before triggering the quickload, snapshot the current in-memory committed `RecordingStore` + `Ledger` to a transient structure. After quickload completes, overwrite the loaded scenario's recording store with the snapshotted current state. The quicksave contributes the world/vessels/career-raw state; the current in-memory state contributes the merged timeline.

This is conceptually the same operation as rewind-to-launch. Today, rewind-to-launch loads the launch save but keeps committed recordings alive so they can play as ghosts from the past timeline. The only new thing here is that the quicksave is mid-flight rather than at launch, and the RewindPoint UT is later than launch UT.

### 5.5 Within a re-fly session

Everything inside the re-fly session is standard Parsek gameplay. The player controls the spawned child vessel; ghosts play from their recordings; ledger replays committed events as UT advances; camera/watch/loop/overlap behavior is unchanged.

If the re-fly itself crosses a multi-controllable split (e.g. A is a self-flying booster that has its own sub-stage), section 5.1 applies recursively: a new RewindPoint is created for that split, its own Unfinished Flight entries will appear on merge. The tree grows naturally; there is no special recursion logic.

### 5.6 Merging a re-fly session

The merge dialog surfaces the re-fly recording. On Merge:

1. The new recording's `MergeState = Merged`.
2. The new recording's trajectory replaces the old BG-crash recording under the same vessel-identity and same parent BranchPoint. Replace-in-place: the Recording's `Id` stays the same, its trajectory fields (`.prec`, `VesselSnapshot`, events, terminal state) are rewritten, and every reference to it from BranchPoints, RewindPoints, Unfinished Flights computation still resolves.
3. If the re-fly produced any new multi-controllable splits, their RewindPoints and child recordings are added normally.
4. The Unfinished Flights group recomputes. The just-merged recording leaves the group (its terminal is no longer BG-crash).
5. If the just-merged recording was the last unmerged child under any RewindPoint, that RewindPoint's quicksave is deleted from disk and the RewindPoint is removed from the scenario. Log a `Verbose` line.

### 5.7 Revert-to-Launch during a re-fly session

Stock KSP Revert-to-Launch during a re-fly session discards the re-fly attempt only. The immutable committed timeline (ABC ghost, C orbit, etc.) survives. The RewindPoint survives.

Mechanism: when the player clicks Revert-to-Launch, detect that the current session is a re-fly (flag set at rewind invocation, cleared on merge/discard). Intercept the revert and show a confirmation dialog explaining the new semantics:

> You are in a Rewind session. Revert will discard this attempt only - your merged flights on the timeline stay intact. Launching a brand new mission from scratch requires going to the Space Center. Revert or Cancel?

On Revert: reload the same RewindPoint's quicksave (not the launch save), re-spawn the child vessel, restart the recording. The player is back at the rewind moment as if they just clicked Rewind again.

If the player instead returns to the Space Center, the re-fly's provisional recording is discarded (not merged), and the RewindPoint remains durable for a future retry. No tree discard.

### 5.8 Unmerged re-fly on session end

If the player closes the game or goes to the Space Center without merging the re-fly, the re-fly's in-progress recording is discarded. The old BG-crash recording remains in its place. The RewindPoint remains durable. Player can invoke Rewind again later.

### 5.9 Save / quickload / quit

Save captures: `ParsekScenario.RewindPoints` (with quicksave filenames), each RewindPoint's quicksave file is already on disk, all Recording state, Ledger state. Reload (including quickload, which is equivalent to "git reset --hard" per existing Parsek semantics): all state restored; RewindPoints still point to valid quicksave files; Unfinished Flights recomputed on first render.

Cross-session persistence: the player can quit KSP entirely, return days later, load the save, open Recordings Manager, see Unfinished Flights, invoke Rewind - the quicksave file has been on disk the whole time.

### 5.10 Tree-discard path

If the player reverts to launch at any point (either from a first-attempt session or from a re-fly session, by bypassing the new dialog's "discard only this attempt" option and going all the way back to pre-launch), the whole recording tree for this launch gets discarded. All RewindPoints under this tree are removed. All quicksave files under this tree are deleted.

This matches existing Parsek behavior: revert-to-launch = discard everything produced since launch. The new feature adds the intercept dialog (section 5.7) but does not change the ultimate semantics of that action.

---

## 6. Edge Cases

Each edge case has: scenario, expected behavior, v1 verdict.

### 6.1 Single-controllable split (debris)

Scenario: SRB drops off with no probe core. Only upper stage has a command module.
Expected: no RewindPoint created. Existing BG debris behavior unchanged. SRB's BG recording has terminal = Destroyed but is not classified as Unfinished Flight (parent BranchPoint's `RewindPointId` is null).
Handled in v1.

### 6.2 Three or more controllable children at one split

Scenario: AB + C stack with side boosters D and E, all decoupled in one frame. Five controllable children.
Expected: one RewindPoint with all five children. Player merges C's orbit; AB, D, E are all in Unfinished Flights. Player picks one, re-flies it, merges; the other two remain in Unfinished Flights. Player continues until all are resolved.
Handled in v1.

### 6.3 Simultaneous split in the same frame as another vessel event

Scenario: joint break and EVA happen in the same physics frame.
Expected: EVA produces its own BranchPoint (`BranchPointType.EVA`); joint break produces its own BranchPoint (`BranchPointType.JointBreak`). Each is evaluated independently for RewindPoint eligibility. EVA produces only one controllable child (the kerbal) plus the original vessel, so it is a multi-controllable split and gets a RewindPoint (this is a natural extension - see 6.4).
Handled in v1 - EVA is already snapshotted today, the only addition is promoting the EVA BranchPoint to RewindPoint status.

### 6.4 EVA as a multi-controllable split

Scenario: player EVAs from a vessel. The kerbal and the vessel are two controllable entities.
Expected: EVA BranchPoint gets a RewindPoint with two children: the EVA kerbal recording and the parent vessel recording (both continue as separate recordings per existing EVA logic).
Handled in v1 as a natural consequence of the same multi-controllable-split rule. The feature is not EVA-specific, but EVA happens to qualify.

### 6.5 Undock as a multi-controllable split

Scenario: two docked vessels separate, both with command modules (common: station + shuttle).
Expected: `BranchPointType.Undock` + multi-controllable -> RewindPoint. Player can Rewind and re-fly whichever vessel they did not focus on originally.
Handled in v1. This is the least novel case - undocking already captures snapshots via `ChainSegmentManager`. The only new thing is promoting it to a Rewind Point and adding the Unfinished Flights UI surface.

### 6.6 Nested splits during a re-fly

Scenario: player re-flies AB, AB stages again into A + B during the re-fly.
Expected: new RewindPoint at split-2, new Unfinished Flights members on merge. Tree grows deeper. No special recursion code.
Handled in v1.

### 6.7 Player aborts a re-fly mid-flight without merging

Scenario: player invokes Rewind on AB, flies for 30 seconds, decides they do not want to continue, returns to Space Center without merging.
Expected: provisional re-fly recording discarded. Old BG-crash recording for AB remains intact. RewindPoint remains durable. Unfinished Flights still contains AB. Player can invoke Rewind again later.
Handled in v1.

### 6.8 Player revert-to-launch during a re-fly (new dialog variant)

Scenario: during a re-fly, player clicks Revert-to-Launch.
Expected: dialog appears explaining new semantics (5.7). Default action reloads the RewindPoint's quicksave, retrying the re-fly. Explicit "full revert to launch" option still exists and discards the whole tree (5.10).
Handled in v1. The dialog text is a design-polish item; placeholder text is in 5.7.

### 6.9 Player quickloads mid-re-fly

Scenario: player hits F9 during a re-fly.
Expected: identical to current Parsek quickload behavior - full state reset to quicksave moment. If the most recent quicksave was the RewindPoint's save (auto-taken at rewind invocation), the re-fly restarts at split UT. If the player took a manual quicksave during the re-fly, F9 loads that. Parsek's recording auto-resume logic handles the re-fly recording continuation as it handles any mid-flight quickload today.
Handled in v1 (leverages existing behavior).

### 6.10 RewindPoint with all children merged

Scenario: player has flown C to orbit, AB as booster to landing, A (from AB's re-fly) as deep-booster to landing. All three immutable. RewindPoint-1 (at split-1) has children {AB, C} both merged.
Expected: RewindPoint-1 has no unmerged children. Its quicksave is deleted; the RewindPoint is removed from `ParsekScenario.RewindPoints`. Unfinished Flights group does not list any child of BranchPoint-1. Attempting to revert to launch would still rewind to the launch save; AB/C/A would play as ghosts.
Handled in v1.

### 6.11 Quicksave file missing or corrupted

Scenario: quicksave file deleted externally or corrupted. Player clicks Rewind.
Expected: file check fails; dialog shows "Rewind unavailable - quicksave missing". RewindPoint is marked `Corrupted = true` in memory. Unfinished Flights row shows the child as "no rewind available" (but still listed, so the player can see they have unmerged flights). Log `Warn`. Corrupt RewindPoints are scheduled for reaping at tree-discard. No auto-purge - the player keeps the awareness.
Handled in v1 with a simple error path; no automatic recovery.

### 6.12 Quicksave save fails at split time (disk full, permissions)

Scenario: joint break fires; RewindPoint creation attempts `GamePersistence.SaveGame`; save throws or returns failure.
Expected: RewindPoint is NOT created. BranchPoint's `RewindPointId` stays null. Log `Warn` explaining the failure. The multi-controllable split proceeds exactly as today (snapshots still taken, BG recording still continues). Player loses the rewind option for this split only. No retry; next split still attempts.
Handled in v1 with a plain failure path.

### 6.13 Re-fly reaches a stable outcome that the player chooses NOT to merge

Scenario: player flies AB booster to a safe landing, opens merge dialog, clicks Discard.
Expected: re-fly recording discarded. Old BG-crash recording remains. RewindPoint remains. Unfinished Flights still contains AB.
Handled in v1 via existing merge-dialog discard path.

### 6.14 Re-fly crashes and player chooses to merge anyway

Scenario: player flies AB booster, crashes. Merge dialog offers "Merge (crashed)" or Discard. Player clicks Merge.
Expected: new crash recording replaces old BG-crash recording. AB now has a different crash trajectory. AB is still an Unfinished Flight? No - user said merging a crash does commit it as immutable (the player explicitly chose). The distinction is not crash-vs-landing, it is merged-vs-unmerged. A merged crash recording is immutable. Unfinished Flights is specifically for BG-recorded, unmerged flights. So post-merge, AB leaves Unfinished Flights, and the only way to retry AB is revert-to-launch.

This is a subtle point worth calling out to the player in the merge dialog: "Merging a crashed flight makes it part of the immutable timeline. You will not be able to Rewind to this split again." v1 ships with that copy in the merge dialog.
Handled in v1 with a warning in the merge dialog.

### 6.15 Two RewindPoints share the same UT (simultaneous multi-controllable splits)

Scenario: two separate vessels both stage in the same frame (rare but possible with heavily choreographed launches).
Expected: two independent RewindPoints, distinct `BranchPointId`s, distinct quicksave filenames. Quicksave contents are the same underlying game state; both files point to equivalent state. Acceptable overhead.
Handled in v1.

### 6.16 Player focuses and switches between controllable siblings during original flight

Scenario: at split-1, player stages, then [ ] switches to AB for a few seconds, then switches back to C.
Expected: Parsek's existing focus-switching logic handles this per the flight recorder design. The focused vessel at merge-time determines the "main" recording. The other controllable sibling is still BG-recorded and still gets its Unfinished Flight entry on merge if it terminated in crash without the player taking it to a stable outcome.
Handled in v1 via existing behavior.

### 6.17 Time warp during a re-fly

Scenario: player warps during a re-fly session to skip atmospheric descent.
Expected: exactly like normal Parsek warp during a flight. Ghosts advance, ledger replays, physics-bubble rules apply. No special re-fly handling.
Handled in v1 via existing behavior.

### 6.18 Re-fly invocation while another scene is loading

Scenario: player clicks Rewind on an Unfinished Flight row while a scene transition is underway.
Expected: button is disabled during scene transitions (existing UI guard). Click is a no-op.
Handled in v1 via existing UI guards.

### 6.19 KSP Revert-to-Launch button repurposed vs. "Return to previous Rewind Point"

Scenario: during a re-fly, player hits Revert-to-Launch. We intercept.
Expected: dialog has three options - "Retry this Rewind Point" (reload RewindPoint quicksave), "Return to launch and discard entire tree", "Cancel". First is new, second is existing Parsek behavior, third is existing.
Handled in v1.

### 6.20 A multi-controllable split whose children both end in the same physical location

Scenario: AB at low altitude stages; A and B both crash within 200 m of each other, both recorded as BG crashes.
Expected: two Unfinished Flights, one per child. Player can rewind either independently. Nothing special required.
Handled in v1.

### 6.21 Re-fly of a vessel that has no fuel or propulsion

Scenario: AB booster has a probe core and no engines (unusual but possible).
Expected: RewindPoint still created (has a controller). Player can invoke Rewind and attempt aero-recovery or parachute landing. No automatic screening of "flyability".
Handled in v1. Player-driven discoverability.

### 6.22 Kerbal on board an Unfinished Flight child

Scenario: crewed booster that stages with kerbals on board (drop pod, escape capsule). Crashes in BG.
Expected: same as controllable-with-probe-core. RewindPoint exists. Player can rewind and fly. Kerbal reservations, reservation lifecycle, and MergeDialog crew handling all behave per existing rules - the re-fly is just another flight with crew.
Handled in v1. Worth a test in-game because kerbal reservation is subtle.

### 6.23 Player attempts to drag an Unfinished Flight into a manual group

Scenario: player tries to move an Unfinished Flight entry into their custom "Mun Missions" group.
Expected: drag rejected. Log `Verbose`. Tooltip explains that Unfinished Flights membership is automatic and resolves when the flight is merged.
Handled in v1.

### 6.24 Mod that modifies joint-break or part-destruction behavior

Scenario: a mod like FerramAerospaceResearch or RealChute interacts with part destruction.
Expected: RewindPoint path depends only on `IsTrackableVessel` check (looks for `ModuleCommand`). Mods that add new command modules are included automatically. Mods that change crash behavior do not affect RewindPoint creation (RewindPoint is captured at split, not at crash).
Handled in v1 with no special-casing.

### 6.25 Extremely high physics warp during a split

Scenario: decoupler fires at 4x physics warp.
Expected: joint-break event still fires, classification still runs, RewindPoint still created. KSP's `GamePersistence.SaveGame` works at physics warp. v1 accepts any slight physics non-determinism that results from save-load at the same warp level.
Handled in v1. Deferred: if this turns out to produce unstable snapshots, fall back to pausing warp during the save.

### 6.26 Disk overhead with many RewindPoints

Scenario: a long career with 50 launches, each with 2 multi-controllable splits, produces 100 quicksave files.
Expected: each quicksave is ~500 KB - 2 MB. 100 saves = 50-200 MB. Acceptable but worth the player knowing about. Add a diagnostic line showing total RewindPoint disk usage in Settings > Diagnostics.
Handled in v1 with visibility. Auto-purging policies deferred.

### 6.27 Player discards the whole tree, then undoes

There is no undo for tree discard; it is destructive. Same as today.
v1 behavior unchanged.

### 6.28 Concurrent save-load during RewindPoint creation

Scenario: auto-save fires on the same frame as a RewindPoint save.
Expected: KSP serializes save operations; they do not concurrency-conflict. Acceptable.
Handled in v1.

### 6.29 What if a non-controllable child becomes controllable later (bizarre mod scenario)

Scenario: a mod adds a probe core to a vessel after it has already been BG-classified as debris.
Expected: not supported. Once the split is classified as single-controllable, the decision is sticky. Re-evaluating on each frame would be wasteful and is not worth it for a rare mod interaction.
Deferred / accepted as a v1 limitation.

### 6.30 UI: how does the player know which Rewind Point corresponds to which booster?

Scenario: long flight with 3 staging events, each produced a controllable sibling that became an Unfinished Flight. Player has 3 entries and needs to pick the one they want to fly.
Expected: each Unfinished Flight row shows:
- Vessel name (from the snapshot's root part name, or player-editable)
- Parent flight name (the sibling that was flown: "booster from Mun Launch I")
- UT of the split (as MET from parent launch: "separated at MET 02:14")
- Altitude / body / situation at split ("dropped at 12 km over Kerbin, suborbital")
- Rewind button, Rename button, Hide button
Handled in v1.

---

## 7. What Doesn't Change

- **Single-controllable splits.** Debris, SRB drops without probe cores, fairing jettisons: no RewindPoint, zero new overhead. Today's path is identical.
- **Merge dialog mechanics.** The merge dialog still lists recordings produced by the session. The new-vs-replace detection happens inside the merge commit, not in the dialog UI.
- **Ghost playback engine.** `GhostPlaybackEngine` reads `IPlaybackTrajectory` per today. The re-fly is just a new live recording that, once merged, becomes just another trajectory for the engine to play.
- **Ledger / GameActions.** Ledger replay on rewind is already what makes career state behave correctly. No new logic needed.
- **BranchPoint schema (mostly).** One optional field added (`RewindPointId`). Existing BranchPoints without that field deserialize as null, meaning "no rewind point", meaning single-controllable or pre-feature.
- **Recording schema.** No changes to `.prec`, `_vessel.craft`, `_ghost.craft`. Replace-in-place rewrites these files under the same recording id.
- **Rewind-to-launch path.** Still works identically. RewindPoints are additive; they do not interfere with the launch save.
- **Loop / overlap / chain playback.** These operate on committed recordings; RewindPoints operate on the tree structure around splits. Orthogonal.
- **CommNet, map view, tracking station ghosts.** Unchanged. A Rewind session is just "flight scene with ghosts", which all those systems already handle.

---

## 8. Backward Compatibility

- **Old saves without RewindPoints.** Load cleanly. `ParsekScenario.RewindPoints` is empty. Every BranchPoint has `RewindPointId = null`. Unfinished Flights group is empty. Player sees no new feature until a new multi-controllable split happens in a future flight.
- **Format version bump.** Parsek's save format version already supports additive fields without bumping. The new `REWIND_POINTS` node under `PARSEK` and the new `REWIND_POINT_ID` key under `BRANCH_POINT` are both optional. No migration code required.
- **Old recordings retroactively promoted?** No. RewindPoints are created at split time. Pre-feature recordings never had a quicksave written at their splits; we cannot synthesize one after the fact. Old Unfinished-equivalent recordings (BG-crashed siblings of merged main flights) remain as they are today - visible in the tree but not rewind-able. Acceptable v1 limitation.
- **Removal.** If a future version removes the feature, RewindPoints and their quicksaves are safely ignorable orphans. Cleanup script can be provided.

---

## 9. Diagnostic Logging

Every decision point in sections 5 and 6 must log. Subsystem tags: `Rewind` for RewindPoint lifecycle, `RewindSave` for quicksave I/O, `UnfinishedFlights` for group membership changes, `RewindUI` for dialog and button interactions.

### 9.1 Lifecycle

- **Create:** `Info` - "[Rewind] Created RewindPoint id=<id> branchPoint=<bpId> ut=<ut> children=[<ids>] quicksave=<filename>"
- **Skip (single controllable):** `Verbose` - "[Rewind] Skipped RewindPoint at branchPoint=<bpId>: only <N> controllable children"
- **Quicksave write:** `Verbose` - "[RewindSave] Wrote <filename> size=<bytes>B elapsed=<ms>ms"
- **Quicksave write failure:** `Warn` - "[RewindSave] Failed to write <filename>: <reason>. BranchPoint=<bpId> will have no RewindPoint."
- **Delete (all children merged):** `Verbose` - "[Rewind] Reaped RewindPoint id=<id>: all <N> children merged. Deleted quicksave <filename>."
- **Delete (tree discard):** `Info` - "[Rewind] Purging <N> RewindPoints on tree discard."

### 9.2 Invocation

- **Click:** `Info` - "[RewindUI] Rewind invoked on recording=<recId> rewindPoint=<rpId>"
- **Quicksave missing:** `Warn` - "[Rewind] Quicksave <filename> missing for rewindPoint=<rpId>. Marking corrupted."
- **Quicksave loading:** `Verbose` - "[Rewind] Loading quicksave <filename> for rewindPoint=<rpId>"
- **Scene entry post-load:** `Info` - "[Rewind] Scene loaded. UT=<ut>. Spawning live vessel from snapshot recording=<recId>"
- **Live vessel spawned:** `Info` - "[Rewind] Live vessel spawned: name=<name> pid=<pid> situation=<situation>"
- **Ghost reconciliation:** `Verbose` - "[Rewind] Reconciled committed state: <N> recordings, <M> ledger actions"

### 9.3 Replace-in-place

- **Replace trigger:** `Info` - "[Rewind] Merging re-fly recording=<recId>. Replacing BG-crash trajectory in place."
- **Old trajectory deleted:** `Verbose` - "[Rewind] Deleted old trajectory files for recording=<recId>: .prec size=<X>B _vessel.craft size=<Y>B"
- **New trajectory written:** `Verbose` - "[Rewind] Wrote new trajectory for recording=<recId>"

### 9.4 Unfinished Flights membership

- **Membership computed:** `Verbose` (per frame, rate-limited shared key) - "[UnfinishedFlights] <N> entries"
- **Entry added:** `Verbose` - "[UnfinishedFlights] Added recording=<recId> vessel=<name> at branchPoint=<bpId>"
- **Entry removed:** `Verbose` - "[UnfinishedFlights] Removed recording=<recId>: terminalKind=<kind> mergeState=<state>"
- **Manual-group drag rejected:** `Verbose` - "[UnfinishedFlights] Rejected drag of recording=<recId> into group=<groupId>: membership is computed"

### 9.5 Revert dialog

- **Dialog shown:** `Info` - "[RewindUI] Revert dialog shown during re-fly. rewindPoint=<rpId>"
- **Retry chosen:** `Info` - "[RewindUI] Retry chosen. Re-loading quicksave for rewindPoint=<rpId>"
- **Full revert chosen:** `Info` - "[RewindUI] Full revert chosen. Discarding tree."
- **Cancel:** `Verbose` - "[RewindUI] Revert dialog cancelled."

---

## 10. Test Plan

Every test has a concrete "what makes it fail" justification. Vacuous existence tests are rejected.

### 10.1 Unit tests (pure logic)

- **RewindPoint round-trip serialization.** Build a RewindPoint, save to ConfigNode, load back, assert field equality. Fails if: ConfigNode key names drift or new fields added without serialization.
- **BranchPoint.RewindPointId round-trip.** Fails if: new optional field not added to OnSave/OnLoad.
- **IsUnfinishedFlight computed property.** Table-driven: terminalKind x mergeState x parentBranchPointHasRewindPoint x controllable. Asserts the property returns true only for the exact combination. Fails if: any wrong combination returns true, any right combination returns false.
- **Multi-controllable-split classifier.** Input: a list of child vessels (with/without ModuleCommand). Output: boolean "should create RewindPoint". Fails if: the zero, one, or two-plus thresholds drift.
- **RewindPoint reaping logic.** Given a RewindPoint with N children in various merge states, assert reap decision. Fails if: reaping triggers too eagerly (before all children merged) or too late (after all merged).

### 10.2 Integration tests (synthetic scenarios)

- **Full re-fly merge round-trip.** Build a synthetic tree with multi-controllable split, one sibling merged as orbit, one as BG-crash. Invoke re-fly flow (without live physics - stub the quicksave load). Merge the re-fly. Assert: old BG-crash recording's trajectory is replaced in place; recording id stable; Unfinished Flights now excludes the merged recording. Fails if: replace-in-place leaks (duplicate recordings, id change, dangling refs).
- **RewindPoint reaping on last-child merge.** Merge child 1, RewindPoint stays. Merge child 2 (last), assert RewindPoint is removed and quicksave file deleted. Fails if: reaping misses the last-child case or fires on non-last merges.
- **Tree discard purges all RewindPoints.** Trigger tree discard on a tree with N RewindPoints. Assert all N quicksave files deleted, `ParsekScenario.RewindPoints` emptied for this tree. Fails if: any quicksave file orphaned, or RewindPoints for other trees touched.
- **Scenario save/load persistence.** Build tree with RewindPoints, save to sfs, clear in-memory, load from sfs, assert structural equality. Fails if: any RewindPoint field lost on round-trip.
- **Quicksave filename validation.** Attempt to create a RewindPoint with a malformed id, assert rejection. Fails if: path-traversal or invalid filename chars slip through.

### 10.3 Log assertion tests (diagnostic coverage)

- **Create path logs.** Trigger a multi-controllable split in a test harness. Assert `[Rewind] Created RewindPoint` appears with correct id. Fails if: log line drifts, or the creation branch is silent.
- **Skip path logs.** Trigger single-controllable split. Assert `[Rewind] Skipped RewindPoint` appears with reason. Fails if: silent skip (debugging blind spot regression).
- **Reap path logs.** Merge final child. Assert `[Rewind] Reaped RewindPoint` appears. Fails if: silent reap.
- **Quicksave-missing path logs.** Stub out the filesystem to return "missing", trigger invocation. Assert `Marking corrupted` line appears. Fails if: silent failure.
- **Unfinished Flights membership change logs.** Merge a child, assert `Removed recording` log appears with correct id. Fails if: membership changes silently.

### 10.4 Edge case tests (one per §6 entry)

- 6.1 Single-controllable split: no RewindPoint. Assert `branchPoint.RewindPointId == null`.
- 6.2 Three-plus children: one RewindPoint, children count matches. Iterate re-flies for each, assert each removes itself from Unfinished Flights on merge.
- 6.4 EVA multi-controllable: EVA BranchPoint gets RewindPoint with vessel + kerbal children.
- 6.5 Undock multi-controllable: same as 6.4 pattern but for undock.
- 6.6 Nested re-fly produces new RewindPoint: assert new RewindPoint added to scenario during re-fly merge.
- 6.10 All children merged: RewindPoint reaped, quicksave deleted.
- 6.11 Corrupted RewindPoint (missing quicksave): invocation fails gracefully, RewindPoint marked corrupted.
- 6.14 Merged crash: recording leaves Unfinished Flights group on merge.
- 6.15 Simultaneous splits: two independent RewindPoints, distinct ids.
- 6.23 Manual-group drag rejected: assert rejection + log line.

### 10.5 In-game tests (runtime, via Ctrl+Shift+T)

- **CaptureRewindPointOnStaging** (category: Recording). Build a synthetic vessel with two decoupled stacks each having a probe core. Trigger decouple via `ModuleDecouple.Decouple`. Wait two physics frames. Assert `ParsekScenario.RewindPoints.Count == 1` and the quicksave file exists on disk.
- **InvokeRewindPointRespawnsLiveVessel** (category: Rewind, scene=FLIGHT). Load a test save with a prepared RewindPoint and one Unfinished Flight. Invoke the rewind flow. Wait for scene re-entry. Assert `FlightGlobals.ActiveVessel` matches the Unfinished Flight's vessel snapshot name and UT matches RewindPoint UT.
- **ReplaceInPlaceOnReFlyMerge** (category: Recording). Complete a re-fly to a safe landing. Invoke merge. Assert the recording id is unchanged pre-and-post-merge, the trajectory file size changed, and Unfinished Flights no longer contains the recording.
- **UnfinishedFlightsGroupRenders** (category: UI). With N=0, N=1, N=3 Unfinished Flights, assert the group collapses/expands and the label shows the correct count.

### 10.6 Performance

- **Quicksave write time.** Assert a RewindPoint quicksave completes in under 500 ms on a reference save. Fails if: a regression pushes into seconds (would disrupt gameplay).
- **Unfinished Flights recompute cost.** Build a tree with 50 RewindPoints and 100 Unfinished Flights. Assert the membership recompute is O(n) and under 5 ms. Fails if: accidentally quadratic.

---

## 11. Implementation Sequencing (Plan-Agent hint)

Rough phase ordering for the Plan agent to consider:

1. **Data model + serialization.** RewindPoint struct, BranchPoint extension, ParsekScenario OnSave/OnLoad. Round-trip tests only, no behavior.
2. **Quicksave write at multi-controllable split.** Extend `DeferredJointBreakCheck` + `DeferredUndockBranch`. Log coverage. In-game test 10.5.1.
3. **Unfinished Flights group (UI only, empty).** Virtual group in `GroupHierarchyStore` + row rendering + membership computation (read-only). Log coverage. UI test 10.5.4.
4. **Rewind invocation flow.** Dialog, quicksave load, live spawn from snapshot, new recording start. In-game test 10.5.2. This is the biggest phase; consider splitting: (a) load + spawn, (b) recording start and reconciliation.
5. **Replace-in-place on re-fly merge.** Hook into merge commit path. Integration test 10.2.1, in-game test 10.5.3.
6. **Reaping and tree discard purge.** Wire the lifecycle. Integration tests 10.2.2 and 10.2.3.
7. **Revert-to-Launch dialog intercept during re-fly.** New dialog variant. UI test + log tests.
8. **Edge cases and polish.** Row UI details (6.30), manual-drag rejection (6.23), diagnostic display of total quicksave disk usage (6.26).

Each phase produces a testable increment. Phases 1-3 ship as a "feature preview" with no gameplay impact; phase 4+ are the actual feature.

---

## 12. Open Questions / Deferred

- **Does `GamePersistence.SaveGame` from inside a Harmony-triggered callback risk a deadlock with other KSP save handlers?** Investigate during implementation. Fallback: queue the save one frame later.
- **Does KSP's physics stabilization after a decouple produce an unstable quicksave if taken on the same frame?** Test empirically. Fallback: wait 1-2 physics frames (matches existing `DeferredJointBreakCheck` behavior).
- **Quicksave compaction.** A KSP save can be large (many MB with many vessels). A long career with many RewindPoints could balloon saves. Defer to post-v1 polish - monitor in-game disk usage display (6.26) and add auto-pruning only if players report it.
- **Overlap of RewindPoint with existing rewind-to-launch "per-recording R button".** The existing R buttons on RecordingStart entries in the Timeline continue to rewind to launch for a specific recording. RewindPoints are mid-mission. Both coexist; no conflict. Worth a UI pass later to visually distinguish.
- **Does the re-fly spawn respect KSP physics easing?** Live spawning from a ProtoVessel snapshot can produce weird physics the first few frames. Existing `VesselSpawner` code already handles this for undock; reuse.
- **Merge dialog copy for "merge a crashed re-fly makes it immutable" warning (6.14).** Final wording in v1 to be drafted during UI phase.
- **"Rename" on Unfinished Flight rows** (6.30). Allowed? If yes, rename persists to the underlying Recording. Nothing novel.

---

*End of design.*
