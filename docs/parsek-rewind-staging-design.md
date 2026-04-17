# Parsek - Rewind-to-Staging Design

*Design specification for Rewind Points on multi-controllable split events (staging, undocking, EVA) and the Unfinished Flights group that lets the player go back to a past split and control a sibling vessel they did not originally fly. Enables the "fly the booster back" gameplay: launch AB, stage, fly B to orbit, merge, then rewind to the staging moment and fly A down as a self-landing booster.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder and timeline systems with mid-mission rewind points anchored at multi-controllable-split BranchPoints.*

**Version:** 0.2 (revised after clean-context review)
**Status:** Proposed.
**Depends on:** `parsek-flight-recorder-design.md` (recording tree, BranchPoint, controller identity, ghost chains, additive-only invariant), `parsek-timeline-design.md` (rewind via quicksave, ledger replay, merge lifecycle), `parsek-game-actions-and-resources-recorder-design.md` (ledger replay semantics for career state, reservation system).
**Out of scope:** Changes to ghost playback engine internals, merge dialog UI internals, orbital checkpointing, ledger recalculation engine, crew reservation manager. Debris splits (fewer than two controllable children) continue to use existing behavior with no rewind point.

---

## 1. Problem

Today, when the player stages a vessel and both halves are controllable (e.g. AB stack splits into booster A and upper stage B, each with a probe core or command pod), Parsek continues recording whichever vessel the player stays focused on. The other vessel is background-recorded until it crashes, at which point its recording is terminal debris. If the player flies B to orbit and merges, A's BG-crash recording is the only record of what A did, and the player has no way to go back and actually fly A as a self-landing booster.

This design adds **Rewind Points** on every split that produces two or more controllable children. A Rewind Point is a durable object that (a) captures a KSP quicksave at the moment of the split classification and (b) retains a split-time ProtoVessel snapshot of each controllable child. Any unfinished child appears in a new **Unfinished Flights** group; invoking its Rewind Point loads the quicksave, spawns the child live from its snapshot, and lets the player fly it. All other vessels (merged siblings, the pre-split parent trajectory) play as ghosts from the timeline. When the re-fly reaches a stable outcome and the player merges, a **new recording is committed as an additive sibling** that supersedes the old BG-crash recording; the old recording stays on disk as hidden history so the ghost-chain monotonic-growth invariant is preserved.

The feature is a natural extension of (a) the existing undock snapshot path (`ChainSegmentManager` already calls `VesselSpawner.TryBackupSnapshot` on undock), (b) the existing rewind-to-launch quicksave path, and (c) the existing additive-tree model where recordings are only added, never removed. The novel piece is the combination: a durable quicksave file anchored on a BranchPoint, plus a "supersedes" pointer that lets the Unfinished Flights view and ghost walker prefer the latest recording for each logical vessel without ever mutating or deleting historical data.

---

## 2. Terminology

- **Split event**: any BranchPoint of type `JointBreak` (decouple/stage), `Undock`, or `EVA` that produces one or more new recordings from a single parent.
- **Controllable entity**: a vessel with at least one `ModuleCommand` part, OR an EVA kerbal. EVA kerbals do not have `ModuleCommand` but carry command authority in the flight-recorder model and so qualify. (The existing `IsTrackableVessel` filter covers both; the wider term "controllable entity" is used here to avoid implying only `ModuleCommand`-bearing vessels.)
- **Multi-controllable split**: a split event with two or more controllable entities emerging (in the EVA case: the vessel the kerbal left + the kerbal; in the decouple case: two or more vessels each with a command module). Only these get a Rewind Point. Single-controllable splits (SRB shell drop without probe core, fairing jettison) continue to behave exactly as today.
- **Rewind Point**: a durable record attached to a multi-controllable split BranchPoint. Holds the quicksave filename, the save's actual UT (see §4.1), and references to each controllable child recording. Survives save/quit/reload.
- **Split-time snapshot**: a ProtoVessel snapshot captured synchronously at the moment the split classifier completes, stored on the child Recording in a field dedicated to this purpose (see §4.3). Separate from the existing `VesselSnapshot` (which is populated at ghost-conversion time, potentially seconds later).
- **Finished recording**: a recording whose `MergeState` is `Immutable`, meaning the player explicitly accepted it as a permanent part of the timeline. A `CommittedProvisional` BG-crash is NOT finished; it is a placeholder awaiting either explicit merge or supersede.
- **Unfinished Flight**: a controllable-child recording whose `MergeState` is `CommittedProvisional` AND `TerminalKind` is `BGCrash` AND is not yet superseded. Listed in the Unfinished Flights group. Rewind-eligible.
- **Unfinished Flights group**: a virtual recording subgroup in the Recordings Manager. Membership is computed per frame from the recording store; not manually editable.
- **Re-fly session**: the gameplay session after invoking a Rewind Point. Starts when the quicksave loads and the live child vessel is spawned; ends on merge (becomes `Immutable`) or on discard (provisional recording discarded).
- **Supersede**: an additive operation that commits a new recording and marks another recording as hidden-from-lookup under a supersede pointer. The superseded recording is NOT deleted or mutated. All user-facing lookups (Unfinished Flights membership, ghost walker, timeline) filter out superseded recordings. Supersede is the mechanism for "replacing" a BG-crash with a landed flight while respecting the flight-recorder's principle 10 ("ghost chain can only grow, never shrink").

---

## 3. Mental Model

### 3.1 Tree evolution across a full flight + re-fly cycle

Below, `(V)` = visible (non-superseded), `(H)` = hidden (superseded). The tree never shrinks; hidden nodes stay on disk but are filtered out of user-facing views.

Starting state: player launches vessel ABC and flies it.

```
[flight in progress, nothing committed yet]
ABC (NotCommitted, live-focused)   (V)
```

Player stages: ABC splits into AB + C. Both have command modules. Multi-controllable split -> quicksave taken, split-time snapshots captured for AB and C. Player stays focused on C.

```
ABC (NotCommitted, terminates at split-1)   (V)
 |
 +-- AB   (NotCommitted, BG-recorded)         (V)
 +-- C    (NotCommitted, live-focused)        (V)
     [RewindPoint-1 anchored @ split-1, SaveUT=saveTime, snapshots: {AB, C}]
```

Player flies C to orbit. AB (unfocused) is BG-recorded and eventually crashes; its BG recording terminates at destruction.

```
ABC (NotCommitted, terminated at split-1)   (V)
 |
 +-- AB   (NotCommitted, BGCrash)              (V)
 +-- C    (NotCommitted, live, at orbit)       (V)
     [RewindPoint-1 still pending commit]
```

Player merges the session. ABC + C are now `Immutable`. AB is `CommittedProvisional` with `TerminalKind=BGCrash` -> Unfinished Flight. RewindPoint-1 stays durable because AB is unmerged-and-unsuperseded.

```
ABC  (Immutable)                  (V)  ghost-playable
 |
 +-- AB  (CommittedProvisional, BGCrash, Unfinished)  (V)  ghost-playable
 +-- C   (Immutable, terminal=Orbit)                   (V)  ghost-playable, chain-tip at orbit
     [RewindPoint-1 @ split-1; Unfinished Flights: {AB}]
```

Player opens Unfinished Flights, sees AB, clicks Rewind. Quicksave loads. UT resets to RewindPoint-1's SaveUT. C becomes a ghost playing its orbit trajectory. ABC's pre-split trajectory is in the past and doesn't play during the session (UT starts already at split-1). AB spawns live from its split-time snapshot. Player takes control. A **new provisional recording** is created for the live AB — call it AB'. AB (the old BG-crash) stays as `CommittedProvisional` + visible until AB' is merged.

```
ABC  (Immutable)                                       (V)
 |
 +-- AB   (CommittedProvisional, BGCrash, Unfinished)  (V)  still visible as ghost reference
 +-- AB'  (NotCommitted, live-focused)                 (V)  the re-fly, in progress
 +-- C    (Immutable)                                   (V)
     [RewindPoint-1 @ split-1]
```

While AB' is live, the ghost engine knows not to also ghost AB at the same identity - existing active-vessel filter handles this (§7). If the player were to abort and discard AB', AB remains the Unfinished Flight and the tree shape returns to the previous diagram.

Player flies AB' for a while, then stages again: AB' splits into A + B. Both have command modules. Multi-controllable split during re-fly -> new quicksave, split-time snapshots for A and B. Player stays focused on B.

```
ABC  (Immutable)                                            (V)
 |
 +-- AB   (CommittedProvisional, BGCrash)                   (V)
 +-- AB'  (NotCommitted, terminates at split-2)             (V)
 |    |
 |    +-- A  (NotCommitted, BG-recorded)                    (V)
 |    +-- B  (NotCommitted, live-focused)                   (V)
 |         [RewindPoint-2 @ split-2]
 +-- C    (Immutable)                                        (V)
     [RewindPoint-1 @ split-1]
```

Player lands B, merges. On merge:
- AB' is committed as `Immutable`, with `Supersedes = AB`.
- AB is marked `SupersededByRecordingId = AB'`. AB stays on disk, but every user-facing lookup treats it as hidden.
- B is `Immutable` with `TerminalKind = Landed`.
- A is `CommittedProvisional` with `TerminalKind = BGCrash` -> new Unfinished Flight.
- Ledger events are added/swapped per §5.6.

```
ABC  (Immutable)                                            (V)
 |
 +-- AB   (CommittedProvisional, BGCrash, Superseded)       (H)
 +-- AB'  (Immutable, terminates at split-2)                (V)  new visible AB-identity
 |    |
 |    +-- A   (CommittedProvisional, BGCrash, Unfinished)   (V)
 |    +-- B   (Immutable, Landed)                           (V)
 |         [RewindPoint-2 @ split-2; Unfinished Flights: {A}]
 +-- C    (Immutable)                                        (V)
     [RewindPoint-1; AB now hidden, all visible-at-RP1 children = {AB'(Immutable), C(Immutable)} -> RP1 eligible to reap]
```

RewindPoint-1's children-for-reap-eligibility: count only visible non-superseded children. AB is hidden, so the visible set is {AB', C} and both are Immutable. RewindPoint-1 is now reap-eligible; quicksave-1 is deleted and RP-1 is removed from `ParsekScenario.RewindPoints`. RewindPoint-2 stays active because A is still an Unfinished Flight.

Player invokes RP-2 for A. Loads quicksave-2. Plays C (orbit) + AB' (up to split-2, then terminates into its children) + B (to landing) as ghosts. A spawns live. Etc.

### 3.2 Key invariants

1. **Additive-only tree growth (inherits from flight-recorder principle 10).** The tree never shrinks. Supersede hides old recordings from user-facing lookups but does not delete them.
2. **One Rewind Point per multi-controllable split; lifetime tied to any visible unmerged child.** RP lives as long as at least one of its registered child recordings is visible (non-superseded) AND `MergeState == CommittedProvisional` AND `TerminalKind == BGCrash`. When the last such child either is merged (becomes Immutable) or is superseded, the RP is reap-eligible and its quicksave is deleted.
3. **A Recording terminates at a stable outcome OR at a split event.** Both forms produce `Immutable` recordings if merged. An unmerged BG-crash terminates at destruction but its `MergeState` is `CommittedProvisional`, not Immutable.
4. **One live vessel per re-fly session.** A RewindPoint may reference multiple snapshots, but invoking it spawns exactly one child live (the one the player selected). Other snapshots remain snapshots; other non-superseded unfinished siblings remain in Unfinished Flights for separate sessions.
5. **Career/ledger state is replayed by the ledger, not preserved across quickload.** After loading the quicksave, KSP's raw career values (funds, science, reputation) drop to the SaveUT value. As UT advances during the re-fly and ghost playback, the ledger reapplies committed events. Whether the player sees values animate gradually or catch up instantaneously at the first post-load frame is a property of the existing rewind-to-launch path and is preserved unchanged (see §5.4).
6. **Ghost-until-tip invariant carve-out for the re-fly focal recording.** Flight-recorder principle 6 says "a vessel claimed by a committed recording stays ghost until the final committed interaction in its lineage completes." During a re-fly of AB, the live AB' vessel is physically present between split-1 and split-2 despite AB's committed recording existing. This is safe because the *new* AB' recording has NOT yet been committed at the moment the player spawns live; AB' is a provisional new Recording, not a resumed committed one. Once AB' is merged and supersedes AB, the ghost-until-tip invariant applies to AB' naturally. The old AB remains hidden and its chain-tip is irrelevant.
7. **Unfinished Flights membership is computed, deterministic from Recording state.** The group is not a manual folder. Membership function: `r.IsVisible && r.MergeState == CommittedProvisional && r.TerminalKind == BGCrash && r.ParentBranchPoint?.RewindPointId != null && r.IsControllable`.

---

## 4. Data Model

### 4.1 RewindPoint

```
RewindPoint
    string  Id                       unique id (e.g. "rp_" + Guid)
    string  BranchPointId            BranchPoint this is anchored to (authoritative)
    double  SaveUT                   Planetarium.GetUniversalTime() at the exact save write
    string  QuicksaveFilename        filename under saves/<save>/Parsek/RewindPoints/
    List<string> ChildRecordingIds   controllable children registered at this split
    DateTime CreatedRealTime         wall-clock, debugging aid only
    bool    Corrupted                true if quicksave validated missing at some point
```

Note on `SaveUT`: the `DeferredJointBreakCheck` classifier runs 1-2 physics frames after the joint-break event. The quicksave is written at the classifier's frame, not at the joint-break frame. `SaveUT` is the actual Planetarium UT at the save-write call. The `BranchPoint.UT` (the joint-break UT) and `RewindPoint.SaveUT` may differ by 1-2 frames. Code that cares about "the moment the player perceives staging" uses `BranchPoint.UT`; code that cares about "the UT the quicksave will load to" uses `SaveUT`.

### 4.2 BranchPoint additions

```
string RewindPointId    null if split had fewer than two controllable children; non-null otherwise
```

Bidirectional link with `RewindPoint.BranchPointId`. Authoritative direction is `BranchPoint.RewindPointId`; if drift is ever detected (load-time consistency check), the BranchPoint wins and the orphaned RewindPoint is logged `Warn` and kept as corrupted.

### 4.3 Recording additions

Three new fields on Recording. Two are persisted; one is persisted only for visible recordings.

```
ConfigNode SplitTimeSnapshot        ProtoVessel at split-classifier-frame. Distinct from VesselSnapshot
                                    (the existing ghost-conversion snapshot). Persisted only for
                                    recordings whose parent BranchPoint has RewindPointId != null.
                                    Filename: <recordingId>_split.craft.

string     SupersededByRecordingId   null if visible; otherwise the id of the recording that
                                     supersedes this one. Set atomically with the supersedor's
                                     creation (see §5.6). NOT cleared by any operation; supersede
                                     is one-way.

enum       MergeState                tri-state replacing the existing binary. Values:
                                     - NotCommitted       session in progress; this recording is
                                                          transient and may be discarded.
                                     - CommittedProvisional  session merged; this recording has a
                                                          terminal state and is on-disk, but is
                                                          NOT part of the immutable timeline. Used
                                                          for BG-crashes of sibling controllables.
                                                          Can be later superseded by a re-fly.
                                     - Immutable          the player explicitly merged this
                                                          recording. Part of the immutable timeline.
                                                          Cannot be superseded (but the player can
                                                          still revert-to-launch and discard the
                                                          whole tree).
```

Computed helpers:

```
bool IsVisible           => SupersededByRecordingId == null
bool IsUnfinishedFlight  => IsVisible
                          && MergeState == CommittedProvisional
                          && TerminalKind == BGCrash
                          && ParentBranchPoint?.RewindPointId != null
                          && IsControllable

TerminalKind note: pre-feature recordings serialized with legacy terminal markers need a migration
to BGCrash on load (see §8). A recording with legacy "terminal=Destroyed" + a BG-recorded history
becomes TerminalKind=BGCrash on first post-upgrade load; the migration is one-way and idempotent.
```

### 4.4 ParsekScenario persistence

New subsection under `PARSEK` scenario node:

```
REWIND_POINTS
    POINT
        id = rp_a1b2c3d4
        branchPointId = bp_x1y2z3
        saveUt = 1742538.43
        quicksaveFilename = rp_a1b2c3d4.sfs
        childRecordingId = rec_01234
        childRecordingId = rec_01235
        createdRealTime = 2026-04-17T21:35:12Z
        corrupted = false
    POINT
        ...
```

Quicksave files live in `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs`. They travel with the save directory and are included in any save-zip export. Filenames are validated against `RecordingPaths.ValidateRecordingId`-equivalent path-traversal rules.

Split-time snapshots live in `saves/<save>/Parsek/Recordings/<recordingId>_split.craft` alongside the existing `<recordingId>_vessel.craft` and `<recordingId>_ghost.craft`.

Recording schema adds the two persisted fields:

```
RECORDING
    ...
    mergeState = CommittedProvisional
    supersededBy = rec_09876      # omitted if null
    splitSnapshotFile = rec_01234_split.craft  # omitted if no split-time snapshot
```

### 4.5 Unfinished Flights UI group

The group is a virtual node in `GroupHierarchyStore` computed each frame by scanning `RecordingStore.CommittedRecordings.Where(r => r.IsUnfinishedFlight)`. Membership is NOT editable; drag-into is rejected with a log and a tooltip. The group's visibility flag is ignored in `GroupHierarchyStore` (§6.31) — the group cannot be hidden, only collapsed. Label: `Unfinished Flights (N)`. Rows sort by parent-recording MET ascending (i.e. the mission order in which unfinished flights accumulated).

---

## 5. Behavior

### 5.1 At a multi-controllable split (recording-time)

Triggered from `ParsekFlight.DeferredJointBreakCheck`, `DeferredUndockBranch`, or the EVA boundary handler, after the existing classification identifies two or more controllable entities emerging from the split.

Sequence:

1. For each controllable child, capture the **split-time snapshot** via `VesselSpawner.TryBackupSnapshot(childVessel)` RIGHT NOW, at the classifier frame. Store in `Recording.SplitTimeSnapshot`. This is distinct from the existing `VesselSnapshot` (populated later at ghost-conversion). For EVA kerbals, snapshot the kerbal's ProtoVessel at the EVA frame.
2. Create a `RewindPoint`. Populate `Id`, `BranchPointId`, `ChildRecordingIds`, `CreatedRealTime`. `SaveUT` is assigned in step 3.
3. Call `GamePersistence.SaveGame(rewindPointId, "persistent", SaveMode.OVERWRITE)` into `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs`. The save is synchronous. Record `SaveUT = Planetarium.GetUniversalTime()` at the call (may differ from `BranchPoint.UT` by 1-2 physics frames — see §4.1).
4. Pause-during-save policy: if `TimeWarp.CurrentRate > 1f`, force `TimeWarp.SetRate(0, instant: true)` before the save and restore afterward. Physics-warp instability is a correctness hazard; §6.25 is hardened relative to v0.1.
5. Write `BranchPoint.RewindPointId`. Verify round-trip (RP's BranchPointId == BP's id). On mismatch, abort RP creation and log `Warn`.
6. Append RP to `ParsekScenario.RewindPoints`.
7. Log `Info` line summarizing the event (see §9).

If the save fails (disk full, permissions, exception): RP is NOT created; `BranchPoint.RewindPointId` stays null; the split proceeds without a rewind opportunity (§6.12). The split-time snapshots for each child are still persisted to `<recordingId>_split.craft` because they are valuable for diagnostic and potentially future retroactive-promotion features (§8).

If fewer than two controllable entities emerge, skip steps 1-6 entirely. Today's path is unchanged. Split-time snapshots are NOT captured for single-controllable splits (the single child follows the existing VesselSnapshot flow).

### 5.2 Between split and merge (flight continues)

The player continues flying the focused vessel. Unfocused controllable children are BG-recorded by the existing background recorder. They accumulate trajectory data until the vessel is destroyed, at which point their recording reaches `TerminalKind = BGCrash`. `MergeState` is still `NotCommitted` during the live session.

On session merge (player confirms at the merge dialog):

- The focused vessel's recording gets its final `TerminalKind` per existing rules; `MergeState` transitions to `Immutable`.
- Every BG-recorded controllable child at each multi-controllable split transitions to `MergeState = CommittedProvisional`. Their `TerminalKind` is whatever BG state they reached (typically `BGCrash`). **They are NOT set to `Immutable`.** This distinction is load-bearing: `CommittedProvisional` flights are eligible to be superseded by re-fly, `Immutable` ones are not.
- The Unfinished Flights group now lists each `IsVisible && CommittedProvisional && BGCrash && ParentBranchPoint.RewindPointId != null` recording.
- RewindPoints: no change. Lifetime = "as long as any child is an Unfinished Flight", see §3.2 invariant 2.

A BG-recorded child that reaches a NON-BGCrash terminal (example: fell, bounced, came to rest on a flat plateau without breaking — technically "landed" from KSP's perspective) is not an Unfinished Flight. It has `TerminalKind = Landed` (or equivalent). It still transitions to `CommittedProvisional` on session merge, but because `TerminalKind != BGCrash`, `IsUnfinishedFlight` returns false. Such a recording is NOT rewind-eligible. The player can promote it to `Immutable` via the merge dialog's explicit "commit" option, or leave it `CommittedProvisional` indefinitely. (This is a rare case; we accept that a non-crashed BG recording of a sibling is pinned at CommittedProvisional unless the player acts. §6.32.)

### 5.3 Invoking a Rewind Point

Player opens Recordings Manager, expands Unfinished Flights, selects an entry, clicks Rewind. Confirmation dialog, content conceptually:

> Rewind to stage separation of <ParentVesselName> at UT <formatted MET from launch>?
> <ChildVesselName> will spawn live at the split moment. <N> previously-merged flights will play as ghosts. Career state will drop to the split moment and re-advance as the timeline replays.
> Your save files are unchanged. This attempt can be discarded at any time. Cancel or Proceed.

On Proceed:

1. Resolve `branchPoint.RewindPointId -> RewindPoint -> QuicksaveFilename`.
2. Verify the quicksave file exists and parses. On failure, set `RewindPoint.Corrupted = true`, show error dialog, abort (§6.11).
3. Verify the `SplitTimeSnapshot` exists for the clicked child. On failure, set RP corrupted, abort.
4. Snapshot current in-memory state that must be preserved across the load (see §5.4 reconciliation table).
5. Invoke the existing rewind-to-quicksave code path (same mechanism as stock F9 / as rewind-to-launch, with a different save filename).
6. Post-load: apply reconciliation per §5.4. Set `RewindContext.IsRewinding = true` with `RewindSessionKind = ReFly` and `ActiveRewindPointId = <rpId>` so downstream systems (revert intercept, merge dialog) can detect the session kind.
7. Spawn the clicked child live from its `SplitTimeSnapshot` at `RewindPoint.SaveUT`. `FlightGlobals.SetActiveVessel(newVessel)`.
8. Create a NEW provisional Recording for the live vessel (`Id = rec_<newGuid>`, `MergeState = NotCommitted`, `ParentBranchPoint = <same as clicked child's parent>`). This is the re-fly recording. Do NOT mutate the clicked child recording yet; it remains the visible BG-crash placeholder until supersede at merge time.
9. Log `Info`.

Step 8 is the key departure from v0.1: the re-fly starts as an additive new Recording, not as a resumption of the clicked child. The supersede pointer is only set at merge time.

### 5.4 Reconciliation: what is preserved across the quickload

The quicksave was written at the split's SaveUT (before any of the currently-committed merges existed). Loading it restores KSP's state to that moment, including the Parsek scenario as it was then. But the player has since committed recordings, added ledger events, reserved kerbals, etc. We must preserve in-memory state that post-dates the quicksave.

**Reconciliation table.** Everything in the "Preserved from in-memory" column is captured pre-load into a transient `RewindReconciliationBundle`, then reapplied after ScenarioModule.OnLoad completes (hook point: the same post-load reconciliation step existing rewind-to-launch uses — `RewindContext.ApplyReconciliation`, extended with a `ReFly` variant).

| Domain | Source after load | Preserved from in-memory |
|---|---|---|
| KSP funds / science / reputation (raw values) | Quicksave | — (replayed by ledger) |
| Planetarium.UT | Quicksave (= SaveUT) | — |
| KSP vessels (including pre-existing unrelated ones) | Quicksave | — (pre-existing vessels at split-time state; re-fly ledger events reapply their post-split modifications) |
| Active vessel | Overwritten by live-spawn of clicked child (§5.3 step 7) | — |
| `ParsekScenario.RewindPoints` | Discarded | Preserved (all RPs, including those created AFTER SaveUT for later splits in subsequently-merged re-flies) |
| `RecordingStore.CommittedRecordings` | Discarded | Preserved (all recordings: Immutable, CommittedProvisional, Superseded — full history) |
| `Ledger.Actions` | Discarded | Preserved (every committed game action, from launch through now) |
| `CrewReservationManager` reservations | Discarded | Preserved (reservations reflect post-merge timeline) |
| `RecordingTree` base state (`PreTreeFunds`, `PreTreeScience`, etc.) | Quicksave | — (these are set at tree creation = launch; quicksave-at-split still has them correctly) |
| `GroupHierarchyStore` (user group hierarchy, visibility flags) | Discarded | Preserved |
| `MilestoneStore` (legacy) | Discarded | Preserved |

After reapplication, re-run `LedgerOrchestrator.Recalculate()` from the tree's pre-tree baseline to catch the raw KSP state up to current timeline values. This is the same post-rewind catch-up that existing rewind-to-launch does; the only difference is we're re-catching-up to mid-timeline rather than launch-only state.

**On the catch-up timing question:** the existing rewind-to-launch path runs the ledger recalculation synchronously at the end of post-load. Raw funds/science/rep snap to their post-ledger values before the first rendered frame. The player does NOT see numbers animate during playback; that was a misunderstanding in v0.1. This revision aligns with actual behavior: values catch up instantly at load, then only the NEW events (from A's re-fly and from any ledger events at UTs that fall during the re-fly) register live.

### 5.5 Within a re-fly session

Standard Parsek gameplay. Live vessel is the spawned child. Ghosts play from committed recordings per existing rules. Ledger events at UTs advancing through the re-fly fire normally. Camera/watch/loop/overlap/chain behavior unchanged.

If the re-fly itself crosses a multi-controllable split, §5.1 applies recursively. A new RewindPoint is created; its child recordings are registered additively; when the re-fly is later merged (committing the parent re-fly as Immutable and superseding the BG-crash), the nested RP is committed alongside, and its BG-crash children become new Unfinished Flights. The tree grows deeper naturally; no special recursion logic.

**Interaction with the existing active-vessel ghost filter.** The ghost playback engine already skips ghosting a recording whose identity matches the active vessel. During a re-fly, the active vessel is the provisional re-fly recording (new id), NOT the BG-crash child (old id). So the engine would, by default, ATTEMPT to also ghost the BG-crash child (same vessel-identity logically but different recording id). The filter must be extended: suppress ghost playback for any recording whose `ParentBranchPoint.RewindPointId` matches the active `RewindContext.ActiveRewindPointId` AND whose logical identity matches the active vessel. In practice this is "the BG-crash child the player is re-flying" — we want that recording hidden visually during its own re-fly. §9 logs this suppression once per session.

### 5.6 Merging a re-fly session (supersede commit)

The merge dialog surfaces the provisional re-fly recording (and any nested new recordings from sub-splits during the re-fly). On Merge:

1. The re-fly recording (AB' in the diagram) transitions `MergeState = Immutable`. Its `Supersedes` pointer (field on the re-fly: `string SupersedesRecordingId`) is set to the BG-crash child's id (AB).
2. The old BG-crash (AB) has `SupersededByRecordingId = AB'.Id` set. It is NOT otherwise modified. Its `.prec`, `_vessel.craft`, `_ghost.craft`, `_split.craft` files remain on disk. Its `MergeState` stays `CommittedProvisional`. Its `TerminalKind` stays `BGCrash`. Only the `SupersededByRecordingId` field changes.
3. Any newly-created nested RewindPoints (from re-fly sub-splits) are added to `ParsekScenario.RewindPoints` with their own BG-crash children (which become new Unfinished Flights).
4. **Ledger event handling (additive with per-recording scope):**
   - All ledger events tied to `AB.Id` (the superseded recording) are marked `Superseded = true`. They are preserved for historical inspection (timeline detail mode can reveal them) but excluded from recalculation. Superseded ledger entries do not affect career totals.
   - All ledger events accumulated during the re-fly session, scoped to `AB'.Id` or its descendants, are committed normally, additively.
   - `LedgerOrchestrator.Recalculate()` runs to recompute totals with the superseded entries excluded.
5. **Milestone / first-time-flag dedup:**
   - A first-time milestone (e.g. "First Splashdown") is credited only if no previously-Immutable or currently-visible-CommittedProvisional recording has already credited it.
   - First-time logic is evaluated against the full committed ledger excluding superseded events.
   - A re-fly that would retroactively "un-credit" a first-time milestone (because the superseded crash had earned it somehow — rare edge case) explicitly does NOT un-credit: first-time flags, once set, are not reverted by supersede. §6.33.
6. **Kerbal reservation / life-death event swap:**
   - If the superseded recording caused a kerbal death ledger event (e.g. pilot in the booster died in the crash), and the re-fly landed that kerbal safely, the death event is marked `Superseded=true` and the recovery event is added. Reputation / rep-decay modules recalculate.
   - `CrewReservationManager` re-resolves reservations: kerbals marked retired-stand-in because of the superseded death are returned to active.
   - §9 logs the death->recover swap explicitly as an `Info` line because this is high-visibility career state.
7. The Unfinished Flights group recomputes. AB leaves (now superseded). Any new BG-crash children under the re-fly's new splits join.
8. **RewindPoint reap check:** for every RewindPoint, recompute the visible-unmerged-BGCrash child count. RPs with count=0 are reap-eligible: delete their quicksave file from disk, remove the RP from `ParsekScenario.RewindPoints`. Log `Verbose`. Note that RP-1 in the §3.1 example reaps on the same merge that creates RP-2's Unfinished Flight for A.
9. Cache invalidation (per-recording caches that were keyed to AB's BG-crash trajectory):
   - No invalidation needed under the supersede model: AB's cached values (`TerminalOrbit`, `TerminalPosition`, `TerrainHeightAtEnd`, chain-tip anchors, ghost mesh) remain correct for AB (they describe its crash trajectory). AB is hidden from user-facing lookups but not corrupted.
   - AB' is a new recording; caches compute against AB's trajectory fresh on first access.

### 5.7 Revert-to-Launch during a re-fly session

Stock KSP Revert-to-Launch during a re-fly discards only the provisional re-fly. The committed timeline survives. RewindPoint survives. Implementation:

- `RewindContext.RewindSessionKind == ReFly` flag is active during the session.
- When Revert-to-Launch is clicked, intercept via the existing UI-lock mechanism Parsek already uses for similar interception. Show a 3-option dialog:

> You are in a Rewind session.
> - Retry this Rewind Point: reload the quicksave and restart the re-fly.
> - Return to launch and discard tree: original Revert-to-Launch behavior. ALL merged flights on this mission timeline will also be discarded.
> - Cancel.

Default highlighted option: Retry. "Return to launch" is a red/destructive style button.

- Retry path: reload the same RewindPoint's quicksave, re-run §5.4 reconciliation, re-spawn the live vessel from the split-time snapshot, start a fresh provisional recording. The previous (failed-and-discarded) provisional recording is dropped.
- Return-to-launch path: invoke the existing revert-to-launch flow. This discards the entire tree, including Immutable recordings. The player is warned one more time. All RewindPoints under this tree are reaped (quicksaves deleted) as part of the tree discard.
- Cancel: close dialog, resume re-fly.

§9 logs every dialog decision.

### 5.8 Unmerged re-fly on session end

If the player returns to the Space Center without merging, or quits the game without merging, the provisional re-fly recording is marked for discard. On next session load:

- Any recording with `MergeState == NotCommitted` whose parent BranchPoint has `RewindPointId != null` AND whose `SupersededByRecordingId == null` is a "zombie provisional re-fly." On load, these are discarded (their sidecar files deleted, the recording removed from `RecordingStore`). Log `Info`.
- The underlying BG-crash child (AB in our example) remains visible, `CommittedProvisional`, `Unfinished`.

In-progress provisional recordings saved into the persistent save via auto-save are handled the same way: on load, zombies are discarded. (This is not a new concept; today's Parsek already has logic for "recording in progress at save time"; extend it to cover the re-fly case.)

### 5.9 Save / quickload / quit

Save captures:
- All `ParsekScenario.RewindPoints` with quicksave filenames.
- All Recording state, including `MergeState`, `SupersededByRecordingId`, `SplitTimeSnapshot` file references.
- All Ledger state, with `Superseded` flags on entries.

Reload (full game quit + load, or F9 quickload within a session): KSP reloads the save. All state reconstructed. Unfinished Flights group recomputes on first render. RewindPoints with missing or unreadable quicksave files are marked `Corrupted`.

F9 during a re-fly session: identical to F9 during any Parsek flight today. The existing auto-resume logic handles mid-flight quickload. If the most recent quicksave was the RewindPoint's save (auto-taken at rewind invocation), F9 restarts the re-fly at SaveUT. If the player took a manual F5 during the re-fly, F9 loads that and resumes the re-fly from there.

Cross-session: player quits and returns days later. RewindPoints and their quicksaves are on disk. Unfinished Flights recomputes. Player invokes Rewind, re-fly proceeds.

### 5.10 Tree-discard path

Revert-to-Launch (bypassing the re-fly dialog) discards the entire recording tree:

- Every Recording under the tree is removed (sidecar files deleted).
- Every RewindPoint under the tree is reaped (quicksave files deleted).
- Every ledger entry scoped to a removed recording is removed.
- Reservation state recomputes.

This matches existing Parsek behavior. The additions in this feature (RewindPoints, quicksaves, split-time snapshots) participate in the existing tree-discard cleanup sweep. No new cleanup logic beyond wiring the new file paths into the sweep.

---

## 6. Edge Cases

Each has: scenario, expected behavior, v1 verdict.

### 6.1 Single-controllable split (debris)
Scenario: SRB drops without probe core. Upper stage has ModuleCommand.
Expected: no RewindPoint. Debris continues today's path. SRB's BG recording has `TerminalKind=Destroyed` but since `ParentBranchPoint.RewindPointId == null`, `IsUnfinishedFlight` returns false.
Handled in v1.

### 6.2 Three or more controllable children at one split
Scenario: stack + two side boosters, all decoupled the same frame, all with probe cores.
Expected: one RewindPoint with all children. Player flies one to completion (merged Immutable), remaining are Unfinished Flights. Player iterates.
Handled in v1.

### 6.3 Simultaneous joint-break and EVA in the same frame
Scenario: decoupler fires AND a kerbal EVAs in the same physics frame.
Expected: two independent BranchPoints (JointBreak and EVA). Each evaluated independently for RewindPoint eligibility.
Handled in v1.

### 6.4 EVA as a multi-controllable split
Scenario: player EVAs. Kerbal + vessel are both controllable entities (see §2 on EVA kerbals qualifying).
Expected: EVA BranchPoint gets a RewindPoint. Split-time snapshot captured for both kerbal and vessel.
Handled in v1.

### 6.5 Undock as a multi-controllable split
Scenario: docked station+shuttle undock, both with commands.
Expected: RewindPoint on Undock BranchPoint. Rewind-able.
Handled in v1.

### 6.6 Nested splits during a re-fly
Scenario: re-fly AB, which itself stages into A+B.
Expected: new RewindPoint at split-2, new children. Tree grows additively. On re-fly merge, both the re-fly (AB') and nested BG-crash children (A) are committed together under the new supersede model.
Handled in v1.

### 6.7 Aborted re-fly (player returns to Space Center without merging)
Scenario: player re-flies AB for 30s, heads to Space Center without merging.
Expected: provisional recording marked for discard. On next session load, discarded. BG-crash AB remains, Rewind remains available.
Handled in v1.

### 6.8 Revert-to-Launch during re-fly
Scenario: player clicks stock Revert-to-Launch during a re-fly.
Expected: 3-option dialog. Retry / Return to launch / Cancel per §5.7.
Handled in v1.

### 6.9 F9 during re-fly
Scenario: player F9s during a re-fly.
Expected: existing quickload behavior + auto-resume logic. The provisional re-fly continues after F9.
Handled in v1.

### 6.10 RewindPoint with all visible children resolved
Scenario: after §3.1's B-merge, RewindPoint-1 has children {AB (Superseded), C (Immutable)}. Visible children = {C}, all Immutable.
Expected: RP-1 is reap-eligible. Quicksave-1 deleted, RP-1 removed from `ParsekScenario.RewindPoints`. Log `Verbose`.
Handled in v1.

### 6.11 Corrupted quicksave (file missing / unreadable)
Scenario: quicksave file deleted externally, or parse fails.
Expected: `RewindPoint.Corrupted = true`. Invocation attempts show "Rewind unavailable" dialog. Row stays in Unfinished Flights (player retains awareness they have unmerged work) but Rewind button is grayed with a tooltip. RP is reaped on tree discard like any other.
Handled in v1.

### 6.12 Quicksave write fails at split time
Scenario: disk full / permissions / exception.
Expected: no RewindPoint created. BranchPoint.RewindPointId stays null. Split proceeds as today (split-time snapshots still saved per §5.1). Log `Warn`.
Handled in v1.

### 6.13 Re-fly to stable outcome, player discards at merge dialog
Scenario: player safely lands AB, opens merge, clicks Discard.
Expected: provisional discarded. BG-crash AB stays. Unfinished Flights still contains AB.
Handled in v1.

### 6.14 Re-fly ends in crash; player merges anyway
Scenario: player crashes the AB re-fly. Merge dialog offers "Merge (crashed)" with a warning.
Expected: re-fly committed as Immutable with TerminalKind=Crashed. Supersedes the old BG-crash AB. Warning in dialog: "Merging a crashed flight replaces the original crash trajectory on the timeline. You can still rewind this mission's other Unfinished Flights, but this one is sealed."
Handled in v1 with dialog copy.

### 6.15 Two simultaneous multi-controllable splits in one frame
Scenario: two independent vessels stage in the same frame (rare but possible).
Expected: two independent RewindPoints. Two quicksave files (same underlying save content) — we accept duplication in v1; dedup via content-hash is §12 deferred.
Handled in v1 with known inefficiency.

### 6.16 Focus-switching between controllable siblings during original flight
Scenario: player stages, [ ]-switches to AB briefly, back to C.
Expected: existing focus-switch logic handles this. Whichever vessel is focused at merge-dialog time is the "main" flight; siblings are BG-recorded.
Handled in v1 (existing behavior).

### 6.17 Time warp during re-fly
Scenario: player warps during re-fly.
Expected: standard Parsek warp. Ghosts and ledger advance.
Handled in v1.

### 6.18 Rewind button clicked during scene transition
Scenario: player clicks Rewind while a scene change is in progress.
Expected: button disabled by existing UI guard. No-op.
Handled in v1.

### 6.19 Revert dialog branch: Full Revert
Scenario: player chose Return-to-Launch from §5.7 dialog.
Expected: existing tree-discard flow runs. All RewindPoints reaped.
Handled in v1.

### 6.20 Two children both crash at the same location
Scenario: AB and C both crash at same-ish coords (e.g. low-altitude abort).
Expected: two separate Unfinished Flights, independent rewinds. No interaction.
Handled in v1.

### 6.21 Re-fly a vessel with no fuel or propulsion
Scenario: AB has a probe core but no engines.
Expected: allowed. Player attempts aero / chute recovery. No flyability screening.
Handled in v1.

### 6.22 Kerbal aboard an Unfinished Flight
Scenario: crewed booster (drop pod). BG-crashed. Player re-flies, lands, merges.
Expected: see §5.6 step 6: the death event is marked Superseded; recovery added; recalculation runs; `CrewReservationManager` returns kerbal from retired-stand-in to active. Rep-loss from death is reversed; rep-gain from recovery applied.
Handled in v1 with explicit ledger swap logic.

### 6.23 Drag Unfinished Flight into manual group
Scenario: player drags row into their "Mun Missions" group.
Expected: drag rejected. Log `Verbose`. Tooltip explains automatic membership.
Handled in v1.

### 6.24 Mods that modify joint-break or part-destruction
Scenario: FAR, RealChute, etc.
Expected: RewindPoint depends only on `IsTrackableVessel`. Mods adding new command module types are auto-included. Crash-behavior mods are orthogonal.
Handled in v1.

### 6.25 High physics warp during a split
Scenario: decoupler fires at 4x physics warp.
Expected: classifier still runs. The save step (§5.1 step 4) forces TimeWarp to 0 before save, restores after. Save is taken at stable physics. §6.25 v0.1 deferred this; v0.2 hardens it because accepting non-determinism in a save-file write is a correctness hazard.
Handled in v1 with warp-0 enforcement.

### 6.26 Disk usage: many RewindPoints across a long career
Scenario: 50 launches, 2 multi-controllable splits each, 100 quicksaves.
Expected: each quicksave ~500KB-2MB. Total 50-200MB. Add a line in Settings > Diagnostics showing total RP disk usage. Auto-purge of reap-eligible RPs happens synchronously at merge; no accumulation of orphan quicksaves beyond user-unmerged ones.
Handled in v1 with visibility.

### 6.27 Tree discard with Unfinished Flights present
Scenario: player has Unfinished Flights, hits Revert-to-Launch (full).
Expected: all RewindPoints under the tree are reaped, Unfinished Flights group empties. Standard tree-discard.
Handled in v1.

### 6.28 Concurrent save-load during RP creation
Scenario: auto-save fires same frame as RP save.
Expected: KSP's save pipeline is single-threaded. Writes to `persistent.sfs` and `RewindPoints/<id>.sfs` serialize. Acceptable.
Handled in v1. Explicit test in §10.

### 6.29 Mod adds a probe core to an already-debris-classified vessel
Scenario: rare mod interaction.
Expected: not supported. Classification at split is sticky.
Deferred / v1 limitation.

### 6.30 UI row content for Unfinished Flight entries
Scenario: player has multiple Unfinished Flights.
Expected: each row shows: vessel name (renameable), parent flight name, "separated at MET X:YY", split situation ("12 km above Kerbin, suborbital"). Rewind / Rename / Hide buttons. Sort by parent-mission MET.
Handled in v1.

### 6.31 Hiding the Unfinished Flights group
Scenario: player attempts to hide the group via the hierarchy store.
Expected: hide is disabled on this group (the hide button / checkbox is absent). Group can be collapsed but never hidden. Rationale: hiding would make rewind points un-discoverable. (Individual rows can be hidden.)
Handled in v1.

### 6.32 Non-crashed BG recording of a sibling
Scenario: AB came to rest on terrain without breaking apart; `TerminalKind = Landed` (BG-determined). Not a crash.
Expected: `IsUnfinishedFlight` is false (TerminalKind != BGCrash). The recording sits at `CommittedProvisional` indefinitely. Player can manually commit it to Immutable via the merge dialog's explicit-commit option, or ignore it. Not rewind-eligible, because the rewind feature is specifically for "re-do a BG crash"; a vessel that BG-landed doesn't need re-doing in this sense.
Handled in v1 as accepted limitation.

### 6.33 First-time milestone earned by superseded recording
Scenario: the superseded BG-crash somehow credited "First Landing on Mun" (ultra-rare: a crashed booster that happened to satisfy a landing check before disintegration). Re-fly lands properly.
Expected: first-time flag is NOT reverted. Both events exist in the ledger; the superseded one is `Superseded=true` and excluded from totals; the re-fly's event is not a first-time (flag already set) so its reward is the non-first-time variant if any.
Handled in v1. Ledger spec: first-time flags are monotonic, never un-set by supersede.

### 6.34 VesselSnapshot references missing mod parts
Scenario: re-fly invoked after uninstalling a mod; SplitTimeSnapshot references a part `PartLoader` no longer knows.
Expected: spawn fails. RewindPoint marked Corrupted. Dialog: "This booster uses parts from a mod that is no longer installed." Log `Warn`.
Handled in v1.

### 6.35 Rewind invocation while flying an unrelated new mission
Scenario: player launched a new mission M2. Now in flight. Opens Recordings Manager, clicks Rewind on an Unfinished Flight from an older mission M1.
Expected: guard dialog: "Invoking Rewind will discard your current mission M2's in-progress state (M2 is not merged). Continue?" On Continue: the current M2 provisional recording is discarded (its tree has no committed siblings, so it's a full tree discard of M2), then M1's rewind proceeds. Alternative behavior worth considering: ban rewind from within an unrelated in-flight mission, require the player to return to Space Center first. v1 picks the guard-dialog option.
Handled in v1 with guard dialog.

### 6.36 Rewind invocation during time warp
Scenario: player is time-warping in Space Center; clicks Rewind.
Expected: rewind flow handles the warp exit as part of the load transition. Same as clicking Rewind from any non-flight scene. Not different from §6.18.
Handled in v1.

### 6.37 Mid-re-fly save-quit-resume cycle without merging
Scenario: player re-flies for 20 minutes, saves via Space Center, quits, resumes next day.
Expected: reload restores the tree with: AB (CommittedProvisional BG-crash), AB' (NotCommitted, in-progress at save time). The NotCommitted provisional is a zombie per §5.8; on load it is discarded. The 20 minutes of re-fly progress are lost. To preserve mid-re-fly work, the player must F5 during the re-fly (which creates a proper KSP quicksave the re-fly can resume from). We accept v1 losing auto-persisted mid-re-fly progress; §12 defers the "promote auto-save to a preserved re-fly state" idea.
Handled in v1 as documented limitation (call out in the UI tooltip: "use F5 to preserve mid-re-fly progress").

### 6.38 Re-fly dock with a Parsek-committed vessel from a different tree
Scenario: re-fly AB rendezvous with station S, which is an Immutable vessel from mission M0 that the player completed earlier.
Expected: station S is a real (non-ghost) vessel because its chain tip has been reached. AB can physically dock with S. This creates a new BranchPoint of type Dock in AB's provisional recording. Since S belongs to a different RecordingTree (M0's), the dock event crosses trees. For v1: the dock event is recorded in the current tree; S is referenced by persistent ID. No formal cross-tree merge occurs; S's trajectory is not modified. On merge, the dock is an additive event in the re-fly's ledger.
Handled in v1 — cross-tree interaction via docking is accepted as "the other tree sees nothing; its station happened to have been docked with in the current tree's re-fly."

### 6.39 Re-fly dock/collide with a pre-existing non-Parsek vessel
Scenario: re-fly AB docks with a station the player placed via a stock KSP launch that Parsek does NOT have a recording for.
Expected: the pre-existing station is a plain KSP vessel. Dock event is recorded in the re-fly's recording. On merge, re-fly becomes Immutable. Subsequent playback of other recordings that happen to involve the same station are not affected — Parsek doesn't track the station's state beyond its use as an interaction target. **Known visual-divergence risk (§6.40 generalizes):** if a separate Parsek recording also recorded an interaction with the same station at a different UT, ghost-playback of both recordings may show inconsistent station positions. Accepted v1 limitation; called out in §12.
Handled in v1 as accepted limitation.

### 6.40 Pre-existing-vessel visual divergence paradox
Scenario: original flight's ghost-B recorded docking with station S at UT=X. In the re-fly, live AB also docks with S at UT=X-100. Station S physically moves due to AB's dock at X-100. At UT=X, ghost-B's playback attempts to dock-visual with S, but S is now in a different place (docked with live AB).
Expected: ghost-B's playback is purely visual; it shows ghost-B arriving at the previously-recorded position and performing the dock animation, possibly floating in empty space. S is not modified by ghost-B. AB's real dock with S is the only physically-true state. This is accepted visual divergence for v1. The design principle: ghost playback is "what this vessel did"; it is NOT "what is physically true now." For the narrow case of stock-vessel interaction, we accept that cross-recording visual consistency is lost.
Handled in v1 as accepted visual limitation. §12 notes post-v1 investigation into freezing the stock-vessel's state at the first Parsek-involved interaction as a hardening step.

### 6.41 Multi-merge dialog listing the re-fly and other new recordings
Scenario: during a re-fly, player used Selective Spawn to materialize a previously-ghost vessel C', then docked with it. The session has the re-fly (AB') and the newly-active C' recording.
Expected: merge dialog lists all new recordings from the session. The re-fly AB' is marked with its Supersedes pointer; other new recordings are additive without supersede. Player accepts all or selectively merges.
Handled in v1 via existing multi-recording merge dialog.

### 6.42 Loop recording on an Unfinished Flight
Scenario: player enabled loop-playback on the BG-crash AB recording (for whatever reason).
Expected: loop-config is a property of the recording. On supersede, AB' inherits no loop config (new recording). AB is hidden; its loop no longer plays. Behavior is consistent with "superseded is hidden."
Handled in v1.

### 6.43 BranchPoint classifier drift across versions
Scenario: post-v1 code changes the heuristic that decides "multi-controllable." A save with older RewindPoints contains RPs whose anchoring BranchPoints now classify as single-controllable.
Expected: old RPs remain valid and reusable; we do not retroactively reclassify. Log `Verbose` on load if drift is detected. (The RP's existence is grandfathered — its children were classified as controllable at the time of split.)
Handled in v1.

### 6.44 Rename on an Unfinished Flight row
Scenario: player renames "booster" row to "Booster-1 (attempt 1)".
Expected: name change persists to the underlying Recording. On supersede, the re-fly is a new Recording with its own default name; the renamed BG-crash is now hidden. Player may want to also name the re-fly; rename is available on the re-fly's row.
Handled in v1.

### 6.45 Hide-row on an Unfinished Flight
Scenario: player hides a specific Unfinished Flight row (not the whole group).
Expected: Recording's `IsHidden` flag set. Row disappears from view but remains an Unfinished Flight (Rewind still available via alternative surface — or, if UI access is only through the group, hiding a row effectively hides the Rewind option). v1 decision: hide on an Unfinished Flight row is supported but surfaces a tooltip warning: "Hiding removes the row from view; Rewind will only be reachable after un-hiding."
Handled in v1.

---

## 7. What Doesn't Change

- **Single-controllable splits.** Same path. Zero overhead.
- **Flight-recorder principle 10 (additive-only tree).** Preserved by supersede-rather-than-mutate. No recording is ever deleted or rewritten after commit.
- **Merge dialog internals.** The dialog still lists the session's recordings; the supersede pointer logic is evaluated at commit time, not in the dialog.
- **Ghost playback engine.** `GhostPlaybackEngine` reads `IPlaybackTrajectory` per today. Hidden recordings are filtered upstream by the ghost walker (it enumerates visible recordings only). No changes to the engine.
- **Active-vessel ghost suppression.** Extended to recognize re-fly identity mapping (§5.5), but the suppression mechanism is existing.
- **Ledger recalculation.** Uses the existing `LedgerOrchestrator.Recalculate()` path with a new filter: superseded ledger entries are excluded from recalculation. The filter is a one-line change; the engine itself is unchanged.
- **Rewind-to-launch path.** Still works identically. RewindPoints and launch-save are additive / coexistent.
- **Loop / overlap / chain playback.** Unchanged. Loop configs on superseded recordings simply stop playing because the recording is no longer visible.
- **CommNet, map view, tracking station ghosts.** Unchanged. Re-fly is flight-scene-with-ghosts, handled by existing systems.
- **Reservation manager (Kerbals).** Extended to recalculate on supersede (swap death->recovery); core lifecycle unchanged.
- **Recording file format.** Sidecar files unchanged in structure. New file types (`<id>_split.craft` and `RewindPoints/<id>.sfs`) are additional, not replacements.

---

## 8. Backward Compatibility

- **Old saves without RewindPoints.** Load cleanly. `ParsekScenario.RewindPoints` empty. All existing BranchPoints have `RewindPointId=null`. All existing Recordings have `SupersededByRecordingId=null`, no `SplitTimeSnapshot`. Unfinished Flights group exists but empty.
- **MergeState migration.** Existing saves use the current 2-state `MergeState` (whatever its concrete values are today). Migration rule on load: today's "committed" = new `Immutable`; today's "in-progress" = `NotCommitted`. No existing recording has `CommittedProvisional` status before this feature. Forward-compatible.
- **TerminalKind = BGCrash is a schema addition.** If today's `TerminalKind` enum does not include a `BGCrash` value, adding it is an enum extension. Old saves with `Terminal=Destroyed` AND a BG-recording lineage are MIGRATED to `TerminalKind=BGCrash` on first post-upgrade load. Migration is idempotent (re-running has no effect). New saves always write `BGCrash` for the same scenario. Pre-feature saves that have such recordings will retroactively appear with BG-crash Unfinished status, BUT since `ParentBranchPoint.RewindPointId` is null (no RP was created for them), `IsUnfinishedFlight` remains false; they stay non-rewindable (correct per §8 v1 policy: no retroactive RP synthesis).
- **Format version.** New scenario fields (`REWIND_POINTS` node, `BRANCH_POINT.rewindPointId`, `RECORDING.mergeState`, `RECORDING.supersededBy`, `RECORDING.splitSnapshotFile`) are optional. No bump required. Readers tolerate missing fields with documented default.
- **Cross-save portability.** The `saves/<save>/Parsek/RewindPoints/` directory travels with the KSP save. Any ZIP-based save-export tool that copies the save directory captures quicksaves too.
- **Feature removal.** If a future version removes the feature, orphan RewindPoint files and extra Recording fields are ignorable. Cleanup is optional.

---

## 9. Diagnostic Logging

Subsystem tags: `Rewind`, `RewindSave`, `Supersede`, `UnfinishedFlights`, `RewindUI`, `LedgerSwap`.

### 9.1 RewindPoint lifecycle

- Create (multi-controllable): `Info` "[Rewind] Created RewindPoint id=<id> branchPoint=<bpId> saveUt=<saveUt> splitUt=<bpUt> children=[<ids>] quicksave=<filename>"
- Skip (single controllable): `Verbose` "[Rewind] Skipped RewindPoint at branchPoint=<bpId>: only <N> controllable children"
- Save written: `Verbose` "[RewindSave] Wrote <filename> size=<bytes>B elapsed=<ms>ms warpWas=<rate>"
- Save failed: `Warn` "[RewindSave] Failed to write <filename>: <reason>. BranchPoint=<bpId> will have no RewindPoint."
- Reap (all visible children resolved): `Verbose` "[Rewind] Reaped RewindPoint id=<id>: visible-unmerged-count=0. Deleted quicksave <filename>."
- Tree discard purge: `Info` "[Rewind] Purging <N> RewindPoints on tree discard for tree=<treeId>."
- Drift detected on load: `Verbose` "[Rewind] RewindPoint id=<id> anchors to BranchPoint=<bpId> whose RewindPointId=<observed> — keeping RP, marking orphan."

### 9.2 Rewind invocation

- Click: `Info` "[RewindUI] Rewind invoked on recording=<recId> rewindPoint=<rpId>"
- Reconciliation bundle captured: `Verbose` "[Rewind] Bundled pre-load: recordings=<N> ledger=<M> reservations=<K> rewindPoints=<R>"
- Quicksave missing/corrupt: `Warn` "[Rewind] Quicksave <filename> unavailable for rewindPoint=<rpId>. Marking corrupted."
- Pre-load snapshot: `Verbose` "[Rewind] Loading quicksave <filename>"
- Post-load reconciliation applied: `Verbose` "[Rewind] Applied reconciliation: restored <N> recordings, <M> ledger actions, <K> reservations"
- Live vessel spawned from SplitTimeSnapshot: `Info` "[Rewind] Live vessel spawned from split-snapshot recording=<bgCrashId> newProvisionalId=<reflyId> situation=<situation>"
- Active-vessel ghost suppressed: `Verbose` (once per session) "[Rewind] Suppressing ghost playback for superseded-pending recording=<bgCrashId> (live re-fly active)"

### 9.3 Supersede on merge

- Supersede trigger: `Info` "[Supersede] Committing re-fly recording=<reflyId> supersedes=<bgCrashId> branchPoint=<bpId>"
- BG-crash marked superseded: `Verbose` "[Supersede] Marked recording=<bgCrashId> superseded by <reflyId>"
- Ledger swap begin: `Info` "[LedgerSwap] Recording=<bgCrashId> had <N> ledger events; marking <M> Superseded=true; re-fly contributed <K> new events"
- Ledger swap kerbal death->recovery: `Info` "[LedgerSwap] Kerbal <name>: death event at UT=<x> marked Superseded; recovery at UT=<y> added. Rep delta reversed."
- Recalculation run: `Verbose` "[LedgerSwap] Recalculation complete: funds delta=<d1>, science delta=<d2>, rep delta=<d3>"
- RP reap check: `Verbose` "[Rewind] Post-merge reap sweep: examined <N> RPs, reaped <M>"

### 9.4 Unfinished Flights membership

- Per-frame recompute (rate-limited shared key): `Verbose` "[UnfinishedFlights] <N> entries"
- Entry added: `Verbose` "[UnfinishedFlights] + recording=<id> vessel=<name> branchPoint=<bpId>"
- Entry removed (merged, superseded, or tree discarded): `Verbose` "[UnfinishedFlights] - recording=<id> reason=<merged|superseded|treeDiscard>"
- Drag rejected: `Verbose` "[UnfinishedFlights] Rejected drag recording=<id> into group=<gId>: membership is computed"

### 9.5 Revert-during-re-fly dialog

- Dialog shown: `Info` "[RewindUI] Revert-during-re-fly dialog shown rewindPoint=<rpId>"
- Retry: `Info` "[RewindUI] Retry chosen; reloading quicksave for rewindPoint=<rpId>"
- Full revert: `Info` "[RewindUI] Full revert chosen; discarding tree=<tId>"
- Cancel: `Verbose` "[RewindUI] Dialog cancelled"

### 9.6 Zombie cleanup on load

- Zombie discarded: `Info` "[Rewind] Discarded zombie provisional re-fly recording=<id> (parent BranchPoint had RewindPointId but recording was NotCommitted on load)"

---

## 10. Test Plan

All tests have "what makes them fail" justifications.

### 10.1 Unit tests

- **RewindPoint round-trip.** Fails if: ConfigNode keys drift, optional-field handling regresses.
- **BranchPoint.RewindPointId round-trip.**
- **Recording new fields round-trip** (MergeState tri-state, SupersededByRecordingId, SplitTimeSnapshot reference).
- **Legacy TerminalKind migration.** Build a save with legacy `Terminal=Destroyed`, BG-recorded descent. Load. Assert TerminalKind migrated to BGCrash. Assert idempotent: re-loading does not double-migrate.
- **IsUnfinishedFlight computed property.** Table-driven over MergeState x TerminalKind x SupersededBy x ParentHasRP x IsControllable. Assert only the exact combination returns true.
- **IsVisible computed property.** Trivial round-trip of SupersededByRecordingId==null.
- **Multi-controllable classifier.** Input: list of children with/without command capability (including EVA kerbal). Assert threshold logic.
- **RP reap eligibility.** Given N children in various (visible, MergeState) combos, assert reap decision.
- **Ledger event filtering by Superseded flag.** Assert recalculation totals exclude superseded entries.
- **First-time milestone monotonic.** Assert supersede does not un-set a first-time flag.

### 10.2 Integration

- **Full supersede round-trip.** Build synthetic tree: ABC, split-1 -> {AB (BG-crash CommittedProvisional), C (Immutable orbit)}. Create provisional AB', commit with Supersedes=AB. Assert: AB.SupersededByRecordingId == AB'.Id. AB'.MergeState == Immutable. Unfinished Flights no longer contains AB. Tree is NOT smaller (both recordings still in RecordingStore).
- **Ledger swap on supersede.** Include a pre-existing death event on AB. Commit AB' with a recovery event. Run recalculation. Assert rep total = original + AB'.recovery (death not double-counted).
- **RP reap on last-visible-child-resolved.** Configure 2 RPs with overlapping children; merge/supersede in sequence. Assert RP-1 reaps on the merge that leaves it with zero visible unmerged BGCrash; RP-2 continues.
- **Tree discard purges all.** N RPs + M quicksave files + K split-snapshots. Trigger tree discard. Assert all files removed, in-memory state purged.
- **Scenario save/load with RPs and superseded recordings.** Round-trip all fields.
- **Zombie cleanup on load.** Persist a save with a NotCommitted provisional re-fly under an RP'd BranchPoint. Load. Assert: provisional discarded; log emitted; BG-crash child remains visible.
- **SplitTimeSnapshot persistence.** Persist split-snapshot, restart, load, verify content.
- **Reconciliation bundle preserves state.** Simulate rewind: capture bundle, clear RecordingStore, simulate load, apply bundle. Assert all preserved fields reappear.

### 10.3 Log assertion tests

- Every decision point in §9 has a test asserting the corresponding log line on its trigger. Silent branches are explicit regressions.
- Specific: supersede commit emits LedgerSwap lines with correct counts.
- Specific: zombie cleanup emits Info log per discarded provisional.
- Specific: active-vessel ghost suppression log appears once per re-fly session, not per frame.

### 10.4 Edge case tests (one per §6)

At least: 6.1, 6.2, 6.4, 6.5, 6.6 (nested), 6.10 (reap), 6.11 (corrupt quicksave), 6.14 (merge crash), 6.15 (simultaneous splits), 6.22 (kerbal death->recovery swap), 6.23 (drag reject), 6.25 (warp-to-0 enforced), 6.31 (can't hide group), 6.32 (non-crash sibling), 6.33 (first-time monotonic), 6.34 (missing mod parts), 6.35 (rewind while unrelated mission active), 6.37 (mid-re-fly save-quit zombies), 6.38 (cross-tree dock), 6.40 (pre-existing vessel visual divergence — assert AB' commits cleanly and ghost-B still plays its recorded trajectory regardless).

### 10.5 In-game tests (Ctrl+Shift+T)

- **CaptureRewindPointOnStaging.** Synthetic vessel, two decoupled stacks with probe cores. Trigger stage. Wait for classifier. Assert: 1 RP, split-snapshots for both children, quicksave on disk.
- **SplitTimeSnapshotMatchesSplitMoment.** Compare `SplitTimeSnapshot.position` against `BranchPoint.UT`-moment vessel position (within one frame's physics tolerance). Fails if: we accidentally used ghost-conversion snapshot instead.
- **InvokeRewindPointSpawnsLive.** Load test save with prepared RP. Invoke. Assert: active vessel matches snapshot name, UT = SaveUT, new provisional recording exists with MergeState=NotCommitted.
- **MergeReFlyCreatesSupersede.** Complete a re-fly; click merge. Assert: new Immutable recording with Supersedes pointer; old BG-crash now has SupersededByRecordingId set.
- **GhostSuppressionDuringReFly.** Verify the BG-crash child is NOT rendered as a ghost during the re-fly (camera-level visual check via `FlightGlobals.Vessels` enumeration).
- **LedgerSwapOnKerbalRecovery.** Crewed booster: seed a death event, run re-fly to safe landing, merge. Assert rep total, kerbal reservation state.
- **UnfinishedFlightsGroupRendering.** N=0, 1, 3. Assert label, sort order, no-hide behavior.
- **WarpZeroedDuringSaveWrite.** Trigger a split at warp=4x. Assert: save was taken at warp=1x AND warp restored to 4x after.

### 10.6 Performance

- **Quicksave write under 500ms.**
- **Unfinished Flights recompute under 5ms** with 100 candidates.
- **Ledger recalculation post-supersede under 100ms** with 10k ledger entries.

---

## 11. Implementation Sequencing

Phase ordering for the Plan agent:

1. **Data model + serialization** (fields, tri-state, migrations). Round-trip tests. Legacy TerminalKind migration.
2. **SplitTimeSnapshot capture** at multi-controllable split. `DeferredJointBreakCheck` / `DeferredUndockBranch` extensions. In-game test 10.5.2.
3. **Quicksave write + RewindPoint creation.** Warp-to-0 guard. Save path plumbing. In-game 10.5.1.
4. **Unfinished Flights group (UI only, read-only).** Membership compute, group hierarchy, no-hide rule, row content. In-game 10.5.7.
5. **Rewind invocation flow (Phase 4a: load + spawn).** Dialog, reconciliation bundle capture, quicksave load, live-spawn from SplitTimeSnapshot. In-game 10.5.3.
6. **Rewind invocation flow (Phase 4b: provisional recording).** New provisional Recording, active-vessel ghost suppression. Log coverage.
7. **Supersede on merge.** Additive commit, SupersededByRecordingId set, ledger event flagging + recalculation. Integration tests.
8. **First-time monotonic, kerbal death->recovery swap.** Specific tests for both.
9. **RewindPoint reap (on merge + tree discard).** Integration tests.
10. **Revert-to-Launch-during-re-fly dialog intercept.** UI + logs.
11. **Zombie cleanup on load.** Persistence test.
12. **Polish: diagnostics disk-usage display, rename, hide-row warning, cross-tree dock edge case docs.**

Phases 1-4 ship as "feature preview" (RPs created, group visible, no invocation). Phase 5+ unlocks the actual feature.

---

## 12. Open Questions / Deferred

- **Harmony-triggered `GamePersistence.SaveGame` deadlock risk.** Investigate empirically; fallback is "save one frame later."
- **Physics stability at split+1 frame (empirical).** `DeferredJointBreakCheck` already waits; assumed sufficient.
- **Cross-recording visual divergence for non-Parsek vessels (§6.40).** Post-v1 candidate: freeze pre-existing vessel state at first Parsek-involved interaction.
- **Auto-persisted mid-re-fly state promotion (§6.37).** v2 candidate: turn auto-save of an in-progress re-fly into a durable re-fly checkpoint. Requires additional lifecycle handling.
- **Content-hash-deduplicated quicksave storage.** For §6.15's simultaneous-splits case. Saves disk if many RPs share identical underlying state.
- **BG-landed sibling manual-commit UI.** §6.32's non-crash sibling at CommittedProvisional needs either a UX or an accepted "dead state" documentation.
- **UI distinction between launch-R and mid-mission-Rewind.** Timeline R buttons vs. Unfinished Flights Rewind are different actions; visually differentiate in v1 UI pass.
- **Physics easing on live-spawn from ProtoVessel.** Existing `VesselSpawner` behavior; verify for split-snapshot spawns.
- **Merge dialog copy** for "merging a crashed re-fly seals it" and "hiding an Unfinished Flight hides Rewind".
- **Cross-tree dock recording semantics (§6.38).** Does the station's tree gain a reverse reference? v1 says no; post-v1 may want a bidirectional registry for consistency.
- **Auto-purge policies for very old reap-eligible RPs.** None in v1; §6.26 just exposes disk usage.

---

*End of design v0.2.*
