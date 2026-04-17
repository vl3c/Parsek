# Parsek - Rewind-to-Staging Design

*Design specification for Rewind Points on multi-controllable split events (staging, undocking, EVA) and the Unfinished Flights group that lets the player go back to a past split and control a sibling vessel they did not originally fly. Enables "fly the booster back" gameplay: launch AB, stage, fly B to orbit, merge, then rewind to the staging moment and fly A down as a self-landing booster.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder, timeline, and ledger systems with mid-mission rewind points anchored at multi-controllable-split BranchPoints.*

**Version:** 0.3 (revised after second clean-context review)
**Status:** Proposed.
**Depends on:** `parsek-flight-recorder-design.md` (recording tree, BranchPoint, controller identity, ghost chains, additive-only invariant), `parsek-timeline-design.md` (rewind via quicksave, ledger replay, merge lifecycle), `parsek-game-actions-and-resources-recorder-design.md` (ledger event model, reservations, career state replay).
**Out of scope:** Changes to ghost playback engine internals, merge dialog UI internals, orbital checkpointing, ledger recalculation engine internals, crew reservation manager internals. Debris splits (fewer than two controllable children) continue to use existing behavior with no rewind point.

---

## 1. Problem

Today, when the player stages a vessel and both halves are controllable (e.g. AB stack splits into booster A and upper stage B, each with a probe core or command pod), Parsek continues recording whichever vessel the player stays focused on. The other vessel is background-recorded until it crashes, at which point its recording is a terminal debris record. If the player flies B to orbit and merges, A's BG-crash recording is the only record of what A did, and there is no in-game way to go back and fly A as a self-landing booster.

This design adds **Rewind Points** on every split that produces two or more controllable entities. A Rewind Point is a durable object that (a) writes a KSP quicksave at the moment of the split classification and (b) retains a split-time ProtoVessel snapshot for each controllable child as a verification artifact. Any unfinished child appears in a new **Unfinished Flights** group; invoking its Rewind Point loads the quicksave, strips KSP's in-world copies of non-selected siblings (replacing them with ghost playback of the committed recordings), activates the selected child from the quicksave's own version of that vessel, and lets the player fly it. When the re-fly reaches an outcome the player accepts, a **new recording is committed additively** with explicit `RecordingSupersede(oldRec, newRec)` relation to the old BG-crash; the old recording stays on disk, and a set of **LedgerTombstone** records surfaces which ledger actions the supersede has retired. The effective career state the player sees is always derived from the same two concepts - the Effective Recording Set and the Effective Ledger Set, defined in §3.

The feature is a natural extension of (a) the existing undock snapshot path (`ChainSegmentManager` calls `VesselSpawner.TryBackupSnapshot` on undock), (b) the existing rewind-to-launch quicksave path, and (c) the additive-only tree invariant (flight-recorder principle 10). The novel pieces are:
- An Effective State Model (§3) that gives every subsystem one rule for "which recordings and ledger actions count right now."
- A persisted `ReFlySessionMarker` on the save file that makes mid-re-fly F5 progress survive quit/load without breaking zombie cleanup for unrelated NotCommitted recordings.
- A post-load vessel-strip step that resolves the duplicated-state problem inherent to "load a quicksave and then place a live vessel" - we use the quicksave's own copy of the selected vessel and strip the other real copies rather than spawning a duplicate.

---

## 2. Terminology

- **Split event**: any BranchPoint of type `JointBreak`, `Undock`, or `EVA`.
- **Controllable entity**: a vessel with at least one `ModuleCommand` part, OR an EVA kerbal. EVA kerbals do not have `ModuleCommand` but carry command authority in the flight-recorder model. (`IsTrackableVessel` covers both.)
- **Multi-controllable split**: a split event with two or more controllable entities emerging. Only these get a Rewind Point.
- **Rewind Point (RP)**: a durable record attached to a multi-controllable split BranchPoint. Holds the quicksave filename, save UT, and a list of ChildSlots. Survives save/quit/reload. Can be **session-provisional** (created during an in-progress re-fly, promoted to persistent on re-fly merge, purged on re-fly discard) or **persistent** (committed).
- **ChildSlot**: a stable logical slot on a RewindPoint that identifies one of the controllable outputs of the split. Each slot has an immutable `OriginChildRecordingId` (the original recording that came out of the split) and a derived `EffectiveRecordingId` (the latest non-superseded recording in the supersede chain rooted at `OriginChildRecordingId`).
- **Split-time snapshot**: a ProtoVessel snapshot captured synchronously at the split classifier frame, stored on the OriginChild Recording as a verification artifact. Not used for live vessel spawn in the normal path (see §6.3); used as a sanity check and as a recovery fallback if the quicksave is unavailable.
- **Finished recording**: a recording whose `MergeState` is `Immutable`.
- **Unfinished Flight**: a recording whose state matches the predicate in §3.1 `IsUnfinishedFlight`.
- **Unfinished Flights group**: a virtual read-only group in the Recordings Manager whose membership is derived from ERS (§3.1).
- **Re-fly session**: a gameplay session started by invoking a Rewind Point. Uniquely identified by a `ReFlySessionId` (GUID) generated at invocation. Ends on merge, discard, retry (new session), or full-revert.
- **Supersede**: an additive recording-level relation. `RecordingSupersede(oldRec, newRec)` means `newRec.SupersedesRecordingId = oldRec.Id` and `oldRec.SupersededByRecordingId = newRec.Id` are set atomically. No recording data is mutated or deleted; lookups filter using the relation.
- **Ledger Tombstone**: an additive ledger record of the form `LedgerTombstone { TombstoneId, SupersededActionId, RetiringRecordingId, UT }` that marks a prior ledger action as retired-by-supersede. Action data is not mutated; ledger recalculation filters using the tombstone list.
- **ReFlySessionMarker**: a serialized field in the `PARSEK` scenario node written when a re-fly session is active. Survives save/load. Validated on load; invalid markers are discarded and logged.
- **ERS / ELS**: Effective Recording Set and Effective Ledger Set. See §3. All career-state-deriving subsystems consume ERS/ELS; nothing reads the raw recording or ledger list directly.

---

## 3. Effective State Model

The feature introduces a single foundational abstraction: every subsystem that asks "what recordings count right now?" or "what ledger actions apply right now?" reads the same two sets. Without this, the feature fans out into N subsystems each with subtly different rules, and paradoxes emerge at the seams.

### 3.1 Effective Recording Set (ERS)

```
ERS = { r in RecordingStore.CommittedRecordings
      : r.IsVisible                                          (not superseded)
        AND r.MergeState in {Immutable, CommittedProvisional} }
```

Helpers:
```
r.IsVisible              := r.SupersededByRecordingId == null
IsUnfinishedFlight(r)    := r in ERS
                            AND r.MergeState == CommittedProvisional
                            AND r.TerminalKind == BGCrash
                            AND r.ParentBranchPoint?.RewindPointId != null
                            AND r.IsControllable
```

The ERS is the authoritative "what recordings contribute to timeline state." Concretely:
- `CommittedProvisional` recordings (BG-crash siblings of merged flights) DO contribute. A BG-crashed booster that the player has not yet re-flown causes a rep penalty; the career UI shows the penalty; the kerbal is retired. If the player never re-flies, that is the effective career state forever.
- `Immutable` recordings contribute normally.
- Superseded recordings do NOT contribute. They are filtered out.
- `NotCommitted` (in-progress) recordings do NOT contribute. They are not committed data.

### 3.2 Effective Ledger Set (ELS)

```
ELS = { a in Ledger.Actions
      : a.ActionId is immutable and stable        (schema precondition)
        AND (a.RecordingId == null
             OR a.RecordingId identifies a recording in ERS)
        AND NOT exists(t in Ledger.Tombstones
                       : t.SupersededActionId == a.ActionId) }
```

Two filter layers:
- **Recording-level**: if a ledger action was scoped to a recording that is now superseded, exclude it. This catches migration/repair/auto-generated actions that might not have individual tombstones.
- **Action-level tombstones**: if an action has a matching tombstone, exclude it. This is the fine-grained supersede mechanism for cases where recording-level scope is insufficient (e.g. a ledger event not cleanly scoped to one recording).

**Both filters run.** An action is effective if and only if BOTH pass.

**Schema precondition.** Every ledger action must have a stable, immutable `ActionId` assigned at write time. This is a v1 addition; existing ledger actions without stable IDs get IDs generated on first post-migration load (see §9 backward compat). After ActionId is set, it never changes.

### 3.3 Session-suppressed subtree (narrow carve-out)

When a re-fly session is active, a NARROW carve-out applies to ONE pair of subsystems only. This is the physics-paradox firewall, not a career-state override.

```
SessionSuppressedSubtree(refly) =
    transitive closure over parent/child edges starting from
    refly.OriginChildRecordingId, restricted to recordings
    whose lineage includes the OriginChild
```

This set is consumed ONLY by:
- **Ghost playback walker**: suppress enumeration of recordings in the set. The ghost of the BG-crash being re-flown and any of its BG-recorded descendants are not rendered during the re-fly.
- **Vessel-identity claim tracker / chain-tip handler**: suppress claims from recordings in the set. This lets the live re-fly vessel exist physically without a paradox collision against the BG-crash's outstanding ghost claim.

Everything ELSE (ledger recalculation, career UI, contract state, kerbal reservations, milestones, resources, timeline view, Unfinished Flights membership) reads ERS and ELS **directly, without the carve-out**. The BG-crash's career effects remain counted during the re-fly. The player sees pre-merge career state that reflects the crash. On merge, the supersede commits and the career effects are retired.

Rationale: inheriting existing Parsek rewind-to-launch behavior, where career state reflects committed recordings regardless of what the player is live-flying. Extending the carve-out to career state would produce inconsistent "un-commits" during re-fly and invent a new state that existing career-state consumers would need to handle.

### 3.4 Subsystem consumer table

| Subsystem | Reads | Writes |
|---|---|---|
| Ghost playback walker | ERS minus SessionSuppressedSubtree (if re-fly active) | never writes |
| Vessel-identity claim tracker | ERS minus SessionSuppressedSubtree (if re-fly active) | never writes |
| Ledger recalculation engine | ELS | derived career state only |
| Career state UI (funds, science, rep display) | ELS-derived totals | never writes |
| Contract state derivation | ELS (filtered to contract action types) | writes contracts through KSP API |
| CrewReservationManager | ERS (for kerbal occupancy walk) | reservations |
| Milestone first-time flags | ELS (filtered to milestone events), monotonic | flag set only, never unset |
| Timeline view | ERS lifecycle events + ELS actions | never writes |
| Resource recalculation | ELS (filtered to resource-changing actions) | never writes |
| Unfinished Flights group | ERS filtered by `IsUnfinishedFlight` | never writes |
| RewindPoint reap logic | ERS children of each RP's ChildSlots | RP removal only |

### 3.5 Invariants

- **Monotonic additive growth preserved.** No recording or ledger action is ever mutated or deleted after commit. Supersede is a relation; tombstones are new records. Principle 10 of the flight-recorder design holds.
- **First-time flags are monotonic.** A first-time milestone flag set at some point is never unset, even if the recording that originally triggered it gets superseded. A re-fly that would also trigger the same first-time event receives the non-first-time reward (if any).
- **Single definition of effective state.** Every subsystem reads from ERS/ELS. No subsystem has its own custom filter. Drift between subsystems becomes impossible by construction.

---

## 4. Mental Model

### 4.1 Tree evolution through a full flight + re-fly + nested-refly cycle

`(V)` = in ERS (visible and not superseded). `(H)` = hidden (superseded; retained on disk but filtered out of all subsystem reads).

Launch:
```
[session in progress, nothing committed yet]
ABC (NotCommitted, live-focused, slot=root)   (V)
```

Staging (split-1): ABC splits into AB + C. Two controllables. RP-1 created as session-provisional with 2 ChildSlots.
```
ABC (NotCommitted, terminated at split-1)         (V)
 |
 +-- [slot-1a: AB]   (NotCommitted, BG-recorded)   (V)
 +-- [slot-1b: C]    (NotCommitted, live-focused)  (V)
     [RP-1 (session-provisional) @ split-1; quicksave=rp1.sfs]
```

Flying C; AB BG-crashes.
```
ABC (NotCommitted, terminated)                    (V)
 |
 +-- AB  (NotCommitted, BGCrash)                   (V)
 +-- C   (NotCommitted, at orbit)                  (V)
     [RP-1 (session-provisional)]
```

Session merge. Recordings become Immutable / CommittedProvisional. RP-1 promotes from session-provisional to persistent.
```
ABC  (Immutable)                                   (V)  ghost
 |
 +-- AB  (CommittedProvisional, BGCrash)           (V)  Unfinished Flight
 +-- C   (Immutable, orbit)                        (V)  ghost/real at chain tip
     [RP-1 (persistent) @ split-1]
     Unfinished Flights = {AB}
```

Player invokes RP-1 selecting AB. `ReFlySessionMarker` written to save. Quicksave rp1.sfs loads. Post-load strip: C's real vessel is removed from the physics world (C is in ERS, it has a recording; render as ghost instead). AB's real vessel from the quicksave stays and becomes the active vessel. A new `NotCommitted` provisional recording AB' is created; re-fly begins.
```
ABC  (Immutable)                                   (V)
 |
 +-- AB   (CommittedProvisional, BGCrash)          (V)  Unfinished [supersede target]
 +-- AB'  (NotCommitted, live-focused,
           supersede-target=AB)                    (V)  provisional re-fly
 +-- C    (Immutable)                              (V)
     [RP-1 (persistent)]
     SessionSuppressedSubtree(AB'.session) = {AB}    <- playback + claim only
     Unfinished Flights = {AB}  (AB still is one; AB' is NotCommitted)
```

During re-fly: ERS = {ABC, AB, AB', C} (all visible). ELS-derived career UI shows AB's crash rep penalty and retired kerbal per §3.3. Ghost walker enumerates ERS minus {AB} = {ABC, C} -> plays those as ghosts; doesn't play AB. Claim tracker suppresses AB's claim; AB' exists physically.

AB' stages again: split-2 produces A + B. RP-2 created (session-provisional, tied to AB'.session).
```
ABC  (Immutable)                                   (V)
 |
 +-- AB   (CommittedProvisional, BGCrash)          (V)
 +-- AB'  (NotCommitted, terminated at split-2)    (V)
 |    |
 |    +-- [slot-2a: A]  (NotCommitted, BG-rec)     (V)
 |    +-- [slot-2b: B]  (NotCommitted, live)       (V)
 |         [RP-2 (session-provisional)]
 +-- C    (Immutable)                              (V)
     [RP-1 (persistent)]
```

Land B, merge. On merge:
- AB' commits as Immutable, `SupersedesRecordingId = AB.Id`.
- AB gets `SupersededByRecordingId = AB'.Id`.
- All ledger actions scoped to AB get LedgerTombstone records (not in-place flags).
- B commits Immutable, Landed.
- A commits CommittedProvisional, BGCrash -> new Unfinished Flight.
- RP-2 promotes session-provisional -> persistent.
- RP-1 reap check: RP-1's ChildSlots = {1a: OriginChild=AB, 1b: OriginChild=C}. Effective recordings: 1a -> AB' (superseded chain from AB), 1b -> C. Both are Immutable. No Unfinished Flight under RP-1. RP-1 is reap-eligible: quicksave deleted, RP removed.
- `ReFlySessionMarker` cleared.

```
ABC   (Immutable)                                  (V)
 |
 +-- AB   (CommittedProvisional, superseded by AB')  (H)
 +-- AB'  (Immutable, terminates at split-2,
           supersedes=AB)                          (V)
 |    |
 |    +-- A  (CommittedProvisional, BGCrash)        (V)  Unfinished
 |    +-- B  (Immutable, Landed)                    (V)
 |         [RP-2 (persistent)]
 +-- C    (Immutable)                               (V)
     [RP-1 reaped]
     Unfinished Flights = {A}
```

Player can now invoke RP-2 to fly A down.

### 4.2 Key invariants

1. **Additive-only tree growth** (flight-recorder principle 10).
2. **RP lifetime** = session-provisional during the session it was created in; on re-fly merge, promoted to persistent; on session discard, purged with the session.
3. **Persistent RP reap** eligibility = no ChildSlot's effective recording is an Unfinished Flight. When the last ChildSlot resolves, the RP is reaped, quicksave file deleted.
4. **One live vessel per re-fly session** (the selected slot's effective recording).
5. **Career state replayed via ELS on rewind.** Values catch up synchronously at load via existing recalculation engine; the player does not see animated counting.
6. **Ghost-until-tip invariant holds for the *new* committed recording** (AB' post-merge behaves like any other Immutable recording). During the session, the carve-out in §3.3 is what lets the live vessel exist.

---

## 5. Data Model

### 5.1 RewindPoint

```
RewindPoint
    string  Id                         unique, "rp_" + Guid
    string  BranchPointId              anchor BranchPoint (authoritative)
    double  SaveUT                     Planetarium.GetUniversalTime() at save write
    string  QuicksaveFilename          relative filename under saves/<save>/Parsek/RewindPoints/
    List<ChildSlot> ChildSlots         one per controllable entity that emerged
    bool    IsSessionProvisional       true if created during an active re-fly session; false = persistent
    string  CreatingSessionId          if session-provisional, the ReFlySessionId it belongs to; else null
    DateTime CreatedRealTime           wall-clock, debugging
    bool    Corrupted                  set to true if quicksave is ever validated missing
```

Session-provisional RPs are cleaned up with the creating re-fly session (discard, zombie-cleanup, or full-revert). Persistent RPs survive across sessions and are reaped per §5.3 rules.

### 5.2 ChildSlot

```
ChildSlot
    int     SlotIndex                  0-based, stable within the RewindPoint
    string  OriginChildRecordingId     the recording created at split for this slot; immutable

Derived (not persisted; computed on demand):
    EffectiveRecordingId()             walk SupersedesRecordingId chain from OriginChild
                                       and return the leaf that is NOT superseded
```

When lookups need the current visible representative for a slot, they call `EffectiveRecordingId()`. This single function is the authoritative "which recording represents slot X now?"

### 5.3 BranchPoint additions

```
string RewindPointId            null if split had <2 controllable entities; non-null otherwise
```

Bidirectional with `RewindPoint.BranchPointId`. Authoritative: `BranchPoint.RewindPointId`. Drift check on load: if RP.BranchPointId -> BP has RewindPointId != RP.Id, the BP wins; the RP is marked Corrupted and logged.

### 5.4 Recording additions

```
enum MergeState
    NotCommitted
    CommittedProvisional
    Immutable

string SupersededByRecordingId       null if visible; else the id of the recording that supersedes this one
string SupersedesRecordingId         null unless this recording supersedes another; the superseded id
                                     (both pointers are set atomically at supersede time; cannot drift)

ConfigNode SplitTimeSnapshot         verification artifact; captured synchronously at split classifier frame
                                     Persisted as sidecar file <recordingId>_split.craft
                                     Only present on recordings born from multi-controllable splits
```

Computed helpers:
```
IsVisible          := SupersededByRecordingId == null
MergeState         := one of the three values above
Supersedes(other)  := this.SupersedesRecordingId == other.Id
IsControllable     := has ModuleCommand OR is an EVA kerbal (per §2)
```

### 5.5 Ledger additions

```
Every LedgerAction now requires:
    string ActionId      stable, immutable, assigned at write time, never changes

New entity:
    LedgerTombstone
        string  TombstoneId                    "tomb_" + Guid
        string  SupersededActionId             the ActionId being retired
        string  RetiringRecordingId            the recording whose commit caused the tombstone
        double  UT                             UT of the recording commit that caused this
        DateTime CreatedRealTime               debugging
```

Tombstones are append-only, never modified or removed. Multiple tombstones for the same `SupersededActionId` are tolerated (re-fly could be superseded again in a future feature; filter just needs "at least one tombstone exists"). v1 ships with "at most one tombstone per action" as a documented but unenforced-at-schema-level expectation.

### 5.6 ReFlySessionMarker

```
ReFlySessionMarker (persisted in PARSEK scenario node, singleton)
    string  SessionId                    unique per invocation; new Guid
    string  TreeId                       the RecordingTree this re-fly belongs to
    string  ActiveReFlyRecordingId       the NotCommitted provisional re-fly Recording id
    string  OriginChildRecordingId       the supersede target (BG-crash being replaced)
    string  RewindPointId                which RP was invoked
    double  InvokedUT                    UT at which the rewind was invoked
    DateTime InvokedRealTime             wall-clock
```

Lifecycle:
- **Written** on Rewind Point invocation, immediately after the quicksave loads and before the re-fly recording is created.
- **Updated** with a new `SessionId` on retry (Revert-during-re-fly dialog, "Retry" option). All other fields stay the same.
- **Cleared** (set to null) on: re-fly merge, re-fly discard, full-revert (tree discard), or load-time validation failure.
- **Validated on every load**: if present, verify:
  - `ActiveReFlyRecordingId` exists, `MergeState == NotCommitted`.
  - `OriginChildRecordingId` exists, is in ERS, `MergeState == CommittedProvisional`, `TerminalKind == BGCrash`.
  - `RewindPointId` exists.
  - `TreeId` matches the parent tree of OriginChild.
  - If any check fails: clear marker, log `Warn`, proceed with normal zombie cleanup.

The marker causes one specific departure from normal zombie cleanup: on load, the `ActiveReFlyRecordingId` is spared from the usual "NotCommitted recordings under an RP-anchored BranchPoint are zombies" sweep (§5.8 of behavior). All other NotCommitted recordings are cleaned up as usual.

### 5.7 ParsekScenario persistence additions

```
PARSEK
    ...
    REWIND_POINTS
        POINT
            id = rp_a1b2c3d4
            branchPointId = bp_x1y2z3
            saveUt = 1742538.43
            quicksaveFilename = rp_a1b2c3d4.sfs
            isSessionProvisional = False
            creatingSessionId =                    # empty for persistent RPs
            corrupted = False
            createdRealTime = 2026-04-17T21:35:12Z
            CHILD_SLOT
                index = 0
                originChildRecordingId = rec_01234
            CHILD_SLOT
                index = 1
                originChildRecordingId = rec_01235
        POINT
            ...
    LEDGER_TOMBSTONES
        TOMBSTONE
            id = tomb_b2c3d4e5
            supersededActionId = act_09876
            retiringRecordingId = rec_02222
            ut = 1742550.11
            createdRealTime = 2026-04-17T21:40:33Z
        TOMBSTONE
            ...
    REFLY_SESSION_MARKER
        sessionId = rf_c3d4e5f6
        treeId = tree_mission17
        activeReFlyRecordingId = rec_03333
        originChildRecordingId = rec_01234
        rewindPointId = rp_a1b2c3d4
        invokedUT = 1742538.43
        invokedRealTime = 2026-04-17T21:38:00Z
```

If no re-fly is active, `REFLY_SESSION_MARKER` is absent from the scenario node.

### 5.8 Directory layout

```
saves/<save>/
    persistent.sfs                                 (KSP standard)
    Parsek/
        Recordings/
            <recordingId>.prec                     (trajectory)
            <recordingId>_vessel.craft             (ghost-conversion snapshot)
            <recordingId>_ghost.craft              (ghost mesh)
            <recordingId>_split.craft              (split-time snapshot, NEW, optional)
        RewindPoints/
            <rewindPointId>.sfs                    (KSP-format quicksave, NEW)
```

All Parsek-owned files live under `saves/<save>/Parsek/` and travel with the save directory (zip export, cross-machine copy, etc.).

### 5.9 Unfinished Flights UI group

Virtual node in `GroupHierarchyStore`, membership derived each frame from ERS filtered by `IsUnfinishedFlight`. Not manually editable. Drag-into rejected. Cannot be hidden (no hide option exposed on this group; only collapsed). Label: `Unfinished Flights (N)`. Rows sort by parent-mission MET ascending.

---

## 6. Behavior

### 6.1 At a multi-controllable split (recording-time)

Triggered from `ParsekFlight.DeferredJointBreakCheck`, `DeferredUndockBranch`, or the EVA handler, AFTER the existing classifier identifies >=2 controllable entities.

Sequence (executed on the classifier frame, then a deferred frame for the save):

1. For each controllable child, capture split-time snapshot via `VesselSpawner.TryBackupSnapshot(childVessel)`. Store as `<recordingId>_split.craft`. (This is a verification artifact; it is not used for live-spawn in the normal path per §6.3.)
2. If a child's snapshot fails to capture, log `Warn` but continue with the other children's snapshots (§7.4 edge case). The RP is still created if at least two children captured successfully; children without a snapshot cannot be live-rewound individually, they get a row in Unfinished Flights with a "partial snapshot - rewind unavailable" indicator (see §7.4).
3. Construct `RewindPoint` object. Populate `Id`, `BranchPointId`, `ChildSlots` (one per controllable entity, with `OriginChildRecordingId`), `CreatedRealTime`. `IsSessionProvisional = true` IF an active re-fly session marker exists; `false` otherwise. `CreatingSessionId = marker.SessionId` in the session-provisional case.
4. **Defer the quicksave write to the next physics frame.** Calling `GamePersistence.SaveGame` inside the classifier callback risks re-entrancy with ScenarioModule OnSave handlers. One-frame defer to a coroutine or a FlightRecorder-driven queue avoids the hazard.
5. On the deferred frame:
   - Force `TimeWarp.SetRate(0, instant:true)` if warp rate > 1. Physics-warp instability is a correctness hazard. Restore warp after the save.
   - Call `GamePersistence.SaveGame(tempName, "persistent", SaveMode.OVERWRITE)` where `tempName` is a transient save name. **KSP writes to the root save directory** per existing code conventions (see FlightRecorder/RecordingStore save-path handling for the established pattern).
   - Atomically move the written file to `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs`. Use `FileIOUtils.SafeMove` (tmp+rename pattern).
   - Record `SaveUT = Planetarium.GetUniversalTime()` at the save call.
6. Write `BranchPoint.RewindPointId`. Verify round-trip.
7. Append RP to `ParsekScenario.RewindPoints` (in-memory; serialized at next save).
8. Log `Info` with all relevant IDs, UTs, and file sizes.

**If the save fails** (exception, disk full, permissions, file lock):
- The RP is NOT created. `BranchPoint.RewindPointId` stays null.
- Split-time snapshots for each child ARE persisted (they are small and independently valuable).
- Log `Warn` with the specific failure reason.
- The split proceeds as today.

**If fewer than two controllable entities emerge**: skip steps 1-7 entirely. Existing behavior unchanged.

### 6.2 Between split and merge (session in progress)

The player flies the focused vessel. Unfocused controllable children are BG-recorded as today. All recordings remain `NotCommitted` during the session.

On session merge:
- The focused vessel's recording -> `Immutable` with final `TerminalKind`.
- Every BG-recorded controllable child whose parent BranchPoint has `RewindPointId != null` -> `CommittedProvisional` (its `TerminalKind` is whatever BG state it reached; typically `BGCrash`).
- All session-provisional RPs promote to persistent (set `IsSessionProvisional = false`, clear `CreatingSessionId`).
- Unfinished Flights group recomputes.
- Ledger events from the session are committed additively; the recalculation engine runs; career UI updates.

### 6.3 Invoking a Rewind Point

Player clicks Rewind on an Unfinished Flight row.

Preconditions checked by UI:
- `RewindPoint.Corrupted == false`.
- Quicksave file exists and parses (cheap pre-check, no load yet).
- No other re-fly session is currently active (see §7.5 edge case for the "session already active" case).

Confirmation dialog (conceptual copy):

> Rewind to stage separation of <ParentVesselName> at UT <formatted MET from launch>?
> <ChildVesselName> will spawn live at the split moment. <N> previously-merged siblings will play as ghosts. The career state you see during this attempt will still reflect the previous crash's penalties - those retire only when you commit the new flight.
> You can discard this attempt at any time. Cancel or Proceed.

On Proceed:

1. Generate `SessionId = Guid.NewGuid()`. Compute `TreeId`, `OriginChildRecordingId` from the clicked row, `RewindPointId` from the anchor.
2. Capture the **reconciliation bundle** (§6.4 table): `RecordingStore`, `Ledger.Actions`, `Ledger.Tombstones`, `RewindPoints`, `CrewReservationManager` reservations, `GroupHierarchyStore` state, `MilestoneStore` legacy entries, `RecordingTree` baselines.
3. Load the quicksave via existing rewind-to-quicksave code path (same mechanism KSP's F9 uses, with a substituted filename).
4. Post-load, on the first frame inside the new scene:
   - Reapply the reconciliation bundle (restore in-memory state that post-dates the quicksave).
   - Write the `ReFlySessionMarker` into `ParsekScenario`.
   - Run the **post-load vessel strip** (§6.4 step 4).
   - Activate the selected child's real vessel (§6.4 step 5).
   - Create the new provisional re-fly Recording (§6.4 step 6).
   - Run `LedgerOrchestrator.Recalculate()` to catch career state up to current timeline values.
   - Log `Info` summary line.

### 6.4 Reconciliation + post-load strip + live vessel activation

This section replaces the v0.2 "spawn from snapshot" flow. The key insight: the quicksave already has the selected child as a real vessel at SaveUT. Don't spawn a new copy; use the existing one and strip the others.

**Reconciliation table** (what is preserved from in-memory vs what is loaded from the quicksave):

| Domain | Post-load source | Preserved from in-memory |
|---|---|---|
| Planetarium.UT | Quicksave (= SaveUT) | — |
| KSP raw funds / science / rep | Quicksave | — (caught up by ledger recalc at end) |
| KSP vessels | Quicksave (all real at SaveUT) | — (see strip, step 4) |
| `RecordingStore.CommittedRecordings` | Discarded | Preserved fully (all Immutable, CommittedProvisional, Superseded - full history) |
| `Ledger.Actions` | Discarded | Preserved |
| `Ledger.Tombstones` | Discarded | Preserved |
| `ParsekScenario.RewindPoints` | Discarded | Preserved |
| `CrewReservationManager` | Discarded | Preserved |
| `GroupHierarchyStore` | Discarded | Preserved |
| `RecordingTree.PreTreeFunds/Science` baselines | Quicksave (correctly set at tree creation = launch) | — |
| `MilestoneStore` (legacy) | Discarded | Preserved |
| `ReFlySessionMarker` | — | Written new (§6.3 step 4) |

**Step 4: post-load vessel strip.** After reconciliation:

For each real KSP vessel in `FlightGlobals.Vessels`:
- Identify which Parsek recording (if any) this vessel belongs to. Matching is done by PID lineage: each recording has a `RootVesselPersistentId` or equivalent, and we compare.
- If the vessel matches an ERS recording other than the selected child:
  - Remove the vessel from the physics world (use existing ghost-conversion path; this is exactly what KSP's on-rails handling does for vessels that go out of physics bubble).
  - Register the vessel's Parsek recording as a ghost for playback.
- If the vessel matches the selected child's OriginChildRecordingId (or any recording in its supersede chain that is visible), keep it as a real vessel. This is the live re-fly target.
- If the vessel does not match any Parsek recording (pre-existing stock vessels, debris), leave it alone. Its state is as of SaveUT; the ledger will reapply any post-SaveUT modifications the committed recordings made to it (see §7.6 edge case for the residual paradox in visual divergence).

Log `Info` with counts: "Stripped N sibling vessels, kept 1 as live, left M unrelated vessels."

**Step 5: activate selected child's vessel.** `FlightGlobals.SetActiveVessel(selectedVessel)`. The PID is whatever the quicksave assigned; we do NOT re-PID it. This matches KSP's normal scene-load behavior.

Sanity check: the selected vessel's situation/position/velocity at SaveUT should match the split-time snapshot within a few frames of physics (the snapshot is the classifier-frame state, the save is the classifier-frame + defer state, typically within 1-2 frames). If the delta exceeds a threshold, log `Warn` - may indicate a drift bug but does not abort the rewind.

**Step 6: create the provisional re-fly Recording.** New `Recording` with fresh `Id`, `MergeState = NotCommitted`, `ParentBranchPoint = <same BranchPoint as OriginChild>`, `SupersedesRecordingId = OriginChildRecordingId` (pre-set, before merge; this lets subsystems know this Recording is "attempting to supersede X" during the session). Add to `RecordingStore`.

Start flight-recorder sampling on this new recording immediately.

### 6.5 Within a re-fly session

Ghost playback walker: enumerates `ERS \ SessionSuppressedSubtree(activeSession)`. SessionSuppressedSubtree is the supersede-target subtree (OriginChildRecordingId and any BG-recorded descendants rooted at OriginChild). Log once at session start: "Suppressing playback for recordings [<list of ids>]". Not logged per frame.

Claim tracker: same carve-out. The supersede-target's vessel-identity claim is suppressed for the session.

Ledger, career UI, contracts, reservations, milestones, timeline, resources, Unfinished Flights: all read ERS/ELS directly. **No carve-out.** During the re-fly, the BG-crash's rep penalty is still in the career UI; the dead kerbal is still in the retired roster (but their live instance can still fly because claim suppression lets them exist physically in the re-fly's vessel); contracts the BG-crash failed are still failed in career state.

Nested splits during the re-fly: §6.1 applies recursively with `IsSessionProvisional = true` on the new RP. Nested RPs promote to persistent on re-fly merge and purge with the session on discard.

### 6.6 Merging a re-fly session

On player clicks Merge in the dialog:

1. For each NotCommitted recording in the session (the re-fly itself + any nested BG-recorded children):
   - If it's the re-fly recording (`SupersedesRecordingId != null`): transition to `Immutable`, ensure `SupersedesRecordingId` is set to the correct target.
   - If it's a BG-recorded nested child whose parent BranchPoint has `RewindPointId != null`: transition to `CommittedProvisional` with BG TerminalKind.
   - Otherwise: transition to `Immutable` with final TerminalKind (normal case).
2. For the supersede-target recording (OriginChild): set `SupersededByRecordingId = newReflyRecording.Id`. This is the only mutation to the old recording and it is one-directional (supersede is never undone in v1).
3. **Ledger tombstone creation.** For each ledger action `a` where `a.RecordingId == OriginChild.Id` OR `a.RecordingId` identifies any recording in the supersede-target subtree:
   - Create a `LedgerTombstone { TombstoneId, SupersededActionId = a.ActionId, RetiringRecordingId = newReflyRecording.Id, UT = current }`.
   - Append to `Ledger.Tombstones`. No existing ledger action is mutated.
   - Log `Info` "[LedgerSwap] Tombstoned <N> actions for supersede-target subtree".
4. **Contract action supersede rules.** The ledger includes contract actions (Accept, Complete, Fail, Cancel). For each contract action tombstoned in step 3:
   - **ContractComplete**: the re-fly retiring a completion means the contract was previously marked complete by the BG-crash recording, but the supersede retires that completion. Contract returns to its pre-completion state (typically Active). If the re-fly independently completes the same contract (via its own ledger events), that new completion applies. If not, the contract stays Active.
   - **ContractFail**: supersede retires the failure. Contract returns to Active (or whatever it was pre-failure). Deadline-derived failures: if the contract's deadline has passed in the current timeline, KSP's contract system will re-fail it on the next refresh. Accepted v1 behavior - we don't resurrect contracts past their deadline.
   - **ContractCancel**: same as ContractFail semantically.
   - **ContractAccept**: supersede retires the acceptance. The contract returns to the offer pool if it's still valid. If the offer pool has rotated past it, the acceptance cannot be restored; log `Warn` and accept this as a v1 limitation.
   - These rules are implemented by the recalculation engine's consumption of ELS: actions tombstoned are simply not applied, and the engine recomputes contract state from the remaining effective actions.
5. **First-time milestone monotonicity.** Walk all tombstoned milestone events. For each first-time flag that was set by a tombstoned event: the flag is NOT unset. If the re-fly independently triggers the same first-time event, it earns the non-first-time reward.
6. **Kerbal reservation re-derivation.** CrewReservationManager recomputes from ERS (now excluding supersede target via `IsVisible` + `MergeState` filters). Dead kerbals whose death event was tombstoned return to active status IF the re-fly's own ledger events show them alive / recovered. The existing reservation walker handles this; no special "swap" logic needed - it's a pure re-derivation from the filtered set.
7. **Promote session-provisional RPs to persistent.** For each RP with `IsSessionProvisional == true AND CreatingSessionId == activeSession.SessionId`: set `IsSessionProvisional = false`, clear `CreatingSessionId`.
8. **Reap check on every persistent RP.** For each persistent RP: walk ChildSlots, compute EffectiveRecordingId, check if any EffectiveRecording is an Unfinished Flight. If none are, RP is reap-eligible: delete quicksave file, remove RP from `ParsekScenario.RewindPoints`. Log `Verbose`.
9. **Clear `ReFlySessionMarker`** (set to null / remove from scenario).
10. **Run `LedgerOrchestrator.Recalculate()`** to propagate career state changes.
11. Log `Info` with the full supersede summary (old id -> new id, N tombstones created, M RPs reaped, ledger deltas).

### 6.7 Revert-to-Launch during re-fly

Stock Revert-to-Launch, if clicked during an active re-fly, is intercepted. Dialog offers:

> You are in a Rewind session.
> - **Retry this Rewind Point**: reload the quicksave and restart this re-fly. Your current attempt is discarded.
> - **Return to launch and discard tree**: original Revert behavior. Your entire mission (including merged flights) is discarded.
> - **Cancel**: resume the re-fly.

On **Retry**:
- Discard the current provisional re-fly recording (and any nested session-provisional children + session-provisional RPs).
- Generate a NEW `SessionId`.
- Update `ReFlySessionMarker.SessionId`; other fields stay.
- Reload the same RP's quicksave via the same code path as §6.3.
- Start a fresh provisional re-fly recording.

On **Full Revert**:
- Clear `ReFlySessionMarker`.
- Invoke the existing tree-discard flow. All recordings under the tree are removed. All persistent RPs under the tree are reaped (quicksaves deleted).
- Session-provisional RPs are purged with the session.

On **Cancel**: close dialog.

### 6.8 Session end without merge (aborted re-fly)

Triggers:
- Player returns to Space Center without merging.
- Player quits the game without merging.

On abort:
- The provisional re-fly recording (`NotCommitted`) is marked for discard. On next load, §5.6 marker validation determines its fate.
- Session-provisional RPs with `CreatingSessionId == activeSession.SessionId` are marked for purge.
- `ReFlySessionMarker` is NOT cleared if the player quits (it persists across the quit; load-time validation handles it).
- If the player returns to Space Center (not quit), clear the marker immediately and discard the provisional recording + session-provisional RPs.

### 6.9 Save / load

Save: captures all of RecordingStore, Ledger.Actions, Ledger.Tombstones, ParsekScenario.RewindPoints (both persistent and session-provisional if session is active), ReFlySessionMarker (if session is active).

Load:
1. KSP loads the save.
2. Parsek ScenarioModule OnLoad populates all state.
3. **Marker validation** (§5.6 rules): if marker is present, verify all fields; on any failure, clear marker + log `Warn`.
4. **Zombie cleanup sweep**: for each recording with `MergeState == NotCommitted`:
   - If `ReFlySessionMarker.ActiveReFlyRecordingId == this.Id`: SPARE. This is the legitimate mid-re-fly F5 resumption case.
   - Otherwise: DISCARD. Delete sidecar files, remove from RecordingStore. Log `Info`.
5. **Session-provisional RP cleanup**: for each RP with `IsSessionProvisional == true`:
   - If the marker is valid AND `CreatingSessionId == marker.SessionId`: SPARE.
   - Otherwise: PURGE. Delete quicksave file, remove from scenario. Log `Info`.
6. Ledger tombstone validation: for each tombstone, verify `SupersededActionId` exists. Orphan tombstones are logged `Warn` but kept (no harm, small memory cost). Purged on tree discard.
7. Unfinished Flights group recomputes.
8. Log one `Info` summary of the load sweep: "<N> zombies discarded, <M> session-provisional RPs purged, <K> orphan tombstones found, <L> RewindPoints loaded".

### 6.10 Tree discard

Full Revert-to-Launch (without an active re-fly, or via the full-revert dialog option) triggers tree discard:
- All recordings under the tree are removed; their sidecar files deleted.
- All RewindPoints under the tree are reaped; quicksave files deleted.
- All ledger actions scoped to tree recordings are removed. Related tombstones are removed.
- CrewReservationManager recomputes.
- `ReFlySessionMarker` cleared.

Standard existing behavior; this feature's additions (RPs, tombstones, split snapshots) participate in the existing cleanup sweep via file-path conventions.

---

## 7. Edge Cases

Each has: scenario, expected behavior, v1 verdict.

### 7.1 Single-controllable split
No RP created. Debris path unchanged. Handled in v1.

### 7.2 Three or more controllable children at one split
One RP with N ChildSlots. Player iteratively re-flies each unfinished slot. Handled in v1.

### 7.3 Simultaneous joint-break + EVA in one frame
Two independent BranchPoints; each evaluated independently for RP eligibility. Handled in v1.

### 7.4 Partial snapshot failure at multi-child split
Scenario: three children at a split; one snapshot capture fails.
Expected: RP is still created if at least two children captured. The failed child's row in Unfinished Flights shows "rewind unavailable - snapshot missing" and its Rewind button is disabled. RP reap ignores unrecoverable slots (they are treated as always-resolved since no user action can affect them). Log `Warn`.
Handled in v1.

### 7.5 Invoking a RP while a re-fly session is active
Scenario: player is re-flying AB. Opens Recordings Manager, clicks Rewind on another Unfinished Flight (from a different split or tree).
Expected: UI guard blocks invocation. Dialog: "You are currently in a Rewind session for <name>. Merge or discard that attempt first." No nested re-fly sessions in v1. Handled in v1.

### 7.6 Re-fly target has BG-recorded descendants
Scenario: AB BG-crashed; on its way down, a piece broke off and got its own BG recording under AB's subtree. Player invokes RP for AB.
Expected: SessionSuppressedSubtree includes AB AND the descendant piece. Neither is played as a ghost nor claims vessel identity during the re-fly. On merge, ALL tombstones for ALL subtree ledger actions are created. Handled in v1.

### 7.7 Pre-existing Parsek-committed vessel from a different tree
Scenario: re-fly AB docks with station S (committed recording from mission M0 in a different RecordingTree).
Expected: station S is real (its chain tip has been reached long ago). Physical dock is allowed. A new Dock BranchPoint is recorded in the re-fly recording. Cross-tree reference is by persistent ID only; M0's tree is not modified. Handled in v1.

### 7.8 Pre-existing non-Parsek vessel (stock/mod station from vanilla launch)
Scenario: re-fly interacts with a stock station the player placed without Parsek recording it.
Expected: standard KSP interaction. Dock event recorded in re-fly recording. No paradox since the station is not under any Parsek claim.
Handled in v1.

### 7.9 Visual divergence for cross-recording stock-vessel interaction
Scenario: ghost-B of the original flight recorded docking with station S at UT=X. During re-fly, live AB also docks with S at UT=X-100. S is physically moved by AB's dock. At UT=X, ghost-B's playback attempts to visually dock with S, but S is elsewhere.
Expected: ghost-B plays in empty space; no physical conflict (ghost has no colliders). Accepted v1 visual limitation. §12 notes post-v1 investigation into freezing stock-vessel state at first Parsek-involved interaction.
Handled in v1 as accepted limitation.

### 7.10 EVA duplicate on load (quicksave has kerbal in source vessel AND in EVA form)
Scenario: player EVAs Jeb from vessel V at split time. Quicksave captured after classifier frame. At save time: Jeb is in EVA form AND may also appear in V's crew manifest depending on exact frame. On rewind load, both forms exist.
Expected: post-load strip detects the duplicate. The EVA form is the canonical controllable entity (it had the classifier's blessing). If V's crew manifest still shows Jeb, remove Jeb from V's crew (this is a KSP-level state fix, not a Parsek recording operation). Log `Verbose` on the dedup. Handled in v1.

### 7.11 Nested RP session-provisional cleanup on parent re-fly discard
Scenario: player re-flies AB, which stages into A+B (creating nested RP-2). Player then discards re-fly without merging.
Expected: RP-2 has `IsSessionProvisional = true`, `CreatingSessionId` = the discarded session's id. On discard (or on load via §6.9 step 5), RP-2's quicksave is deleted and RP-2 is removed. No orphans. Handled in v1.

### 7.12 Direct load of F5 quicksave taken mid-re-fly
Scenario: player F5's mid-re-fly, quits, comes back, directly loads the F5 quicksave.
Expected: the F5 quicksave was a full KSP save written at re-fly time. It includes the `ReFlySessionMarker` (persisted in ParsekScenario at that moment). On load, marker validates successfully: `ActiveReFlyRecordingId` exists in the loaded scenario as NotCommitted, other checks pass. The NotCommitted re-fly recording is SPARED from zombie cleanup. Re-fly resumes. Handled in v1.

### 7.13 F5-taken-outside-re-fly, loaded during active re-fly (irrelevant? probably)
Scenario: player F5's before any re-fly; later starts a re-fly; then F9s from that pre-re-fly quicksave.
Expected: F9 reverts ALL state to the F5 moment. No re-fly is active; no marker. Standard behavior. Handled in v1.

### 7.14 Supersede target had contract Accept action
Scenario: the BG-crash recording somehow accepted a contract mid-flight (rare, contracts usually accept at KSC).
Expected: tombstone for the Accept. Contract returns to offer pool if pool still holds it; if rotated out, contract is lost. §6.6 step 4. Log `Warn`. Handled in v1 with documented limitation.

### 7.15 Supersede target had facility/strategy action
Scenario: an auto-generated facility repair ledger action was scoped to the BG-crash recording.
Expected: tombstone via recording-level scope (§3.2 filter 1). Facility repair is retired; recalc restores the facility's prior state. Handled in v1.

### 7.16 Kerbal death + recovery via supersede
Scenario: crewed BG-crash killed Jeb. Re-fly landed safely.
Expected: death event tombstoned (ActionId-level). Jeb's retired status re-derives via reservation walker from ERS; recovered. Rep delta recomputes. §6.6 step 6. Handled in v1.

### 7.17 Re-fly crashes; player merges
Scenario: player crashes the re-fly and clicks Merge in the merge dialog.
Expected: re-fly commits Immutable, Crashed. Supersedes the original BG-crash. Original BG-crash's ledger actions are tombstoned; new crash's ledger actions apply. Merge dialog warning: "Merging a crashed flight replaces the original crash. You will not be able to Rewind this split again." Handled in v1.

### 7.18 Re-fly reaches stable outcome, player discards
Scenario: player lands safely, opens merge, clicks Discard.
Expected: provisional re-fly discarded; marker cleared; original BG-crash visible; RP durable. Handled in v1.

### 7.19 Simultaneous multi-controllable splits in one frame
Scenario: two independent vessels stage same frame.
Expected: two RPs, distinct ids, each anchored to a different BranchPoint. Quicksave written per RP. §6.1 queues serialize the saves (no concurrency). Handled in v1.

### 7.20 F9 during re-fly
Scenario: player F9s during re-fly. The most recent quicksave depends on timing.
Expected: F9 reverts to whichever quicksave is most recent. If that's the RP quicksave (auto-taken at invocation), re-fly restarts at SaveUT. If it's a manual F5 (§7.12), re-fly resumes from there. Parsek's existing F9 auto-resume handles this. Handled in v1.

### 7.21 Warp during re-fly
Standard Parsek warp. Ghosts advance via ERS enumeration (with session suppression). Handled in v1.

### 7.22 Rewind click during scene transition
Existing UI guard blocks. Handled in v1.

### 7.23 Two children crash at same location
Two Unfinished Flights. Independent rewinds. Handled in v1.

### 7.24 Re-fly a vessel with no engines
Allowed. Player attempts aero / chute. No flyability screening. Handled in v1.

### 7.25 Kerbal aboard Unfinished Flight
See §7.16. Handled in v1.

### 7.26 Drag Unfinished Flight into manual group
Rejected with tooltip. Handled in v1.

### 7.27 Mods changing joint-break or destruction
Classifier path is mod-agnostic re ModuleCommand. Handled in v1.

### 7.28 High physics warp at split
§6.1 forces warp to 0 during save. Handled in v1.

### 7.29 Disk usage tracking
Settings > Diagnostics line showing total RP disk usage. No auto-purge; persistent RPs are reaped only by the merge/tree-discard rules. Handled in v1.

### 7.30 Mod-part missing on re-fly spawn
SplitTimeSnapshot (and the quicksave) reference parts from an uninstalled mod. Load fails or vessel spawns broken.
Expected: detect PartLoader failures, mark RP Corrupted, show error dialog. Handled in v1.

### 7.31 Cannot-hide Unfinished Flights group
The group has no hide option. Only collapsible. Handled in v1.

### 7.32 Non-crashed BG sibling
Sibling BG-recording reached a non-BGCrash terminal (`Landed`, etc.). Sits at `CommittedProvisional` indefinitely; not in Unfinished Flights (predicate requires `TerminalKind == BGCrash`). Player can manually commit to Immutable via merge dialog's explicit-commit option. Accepted v1 limitation.

### 7.33 First-time flag set by superseded recording
First-time flag set at UT<crash. Re-fly supersedes and crash event is tombstoned. First-time flag is NOT unset (§3.5 monotonicity). Re-fly's own first-time attempt gets non-first-time reward. Handled in v1.

### 7.34 Classifier drift across versions (RP whose BP no longer classifies multi-controllable)
Old RPs retained and usable; no retroactive reclassification. Log `Verbose` on load. Handled in v1.

### 7.35 Rename / hide on Unfinished Flight row
Rename: persists to Recording name; affects the supersede-target. Hide row: allowed, with tooltip warning that rewind becomes hard to reach. Hiding the group itself: disabled (§7.31). Handled in v1.

### 7.36 Corrupted quicksave discovered at invocation
RP marked Corrupted. Row stays in Unfinished Flights; Rewind button disabled with tooltip. Handled in v1.

### 7.37 Auto-save concurrent with RP save
§6.1 defers to a clean frame and uses queued write. Save writes are serial through KSP's pipeline. Handled in v1 with explicit test.

### 7.38 Rewind invocation while flying unrelated new mission
Scenario: player launched M2, in flight with M2's provisional recording. Clicks Rewind on an Unfinished Flight from M1.
Expected: guard dialog. "Starting a Rewind session will discard your current mission <M2 name>'s in-progress state. Continue?" On Continue: M2's tree (which is also purely provisional) is tree-discarded; then M1's rewind proceeds. Handled in v1.

### 7.39 Pre-existing vessel reservation conflict during re-fly
Scenario: station S has Jeb reserved per the original timeline. Live re-fly has Jeb onboard the booster.
Expected: reservation manager sees two concurrent reservations for Jeb. Session suppression doesn't extend to reservation (narrow carve-out). This is a known conflict; v1 documents it - the reservation manager reports the conflict but does not prevent the re-fly. Accepted v1 limitation; post-v1 may need finer-grained reservation scoping.

### 7.40 Transaction interruption during supersede merge
Scenario: merge is in progress (recording fields being set, tombstones being appended, RP reaps firing) when an autosave fires or an exception is thrown.
Expected: all merge operations are grouped into a single transaction-like sequence that runs before any serialization. Within the sequence, field mutations happen in-memory only; persistence happens atomically at the end via ScenarioModule OnSave (which KSP calls asynchronously but synchronously relative to the single save operation). Intra-sequence exceptions trigger rollback: the provisional re-fly stays NotCommitted, tombstones not written. Log `Error`. Handled in v1 with explicit try/catch + rollback.

### 7.41 Pruning old split snapshots
`<recordingId>_split.craft` files are kept as long as the Recording exists. Superseded recordings' snapshots are kept (they are small). On tree discard, all sidecar files are removed. v1 does not prune superseded snapshots individually. Handled in v1.

---

## 8. What Doesn't Change

- **Flight-recorder principle 10** (additive-only tree). Preserved by supersede-as-relation + tombstone-not-flag.
- **Single-controllable split path.** Zero new overhead.
- **Merge dialog internals.** Dialog lists session recordings; supersede decisions happen at commit time.
- **Ghost playback engine.** Reads ERS via the walker; `SessionSuppressedSubtree` is a filter in the walker, not in the engine.
- **Active-vessel ghost suppression.** Extended via ERS filter; the existing "don't ghost the active vessel" logic is a natural special case of ERS minus ActiveVesselIdentity.
- **Ledger recalculation engine.** Reads ELS; no engine change; only the input filter is new.
- **Rewind-to-launch.** Unchanged path. Coexists with RPs.
- **Loop / overlap / chain playback.** Reads ERS via standard channels; hidden recordings simply stop playing.
- **CommNet, map view, tracking station.** Ghost lifecycle unchanged.
- **Recording file format.** Sidecar format additive (`_split.craft` is new, optional). No existing file changes structure.
- **Reservation manager internals.** Re-derivation from ERS is the existing mechanism; filter expansion is the only addition.

---

## 9. Backward Compatibility

- **Old saves without RPs.** Load cleanly. ERS, ELS, Unfinished Flights all empty re this feature.
- **MergeState migration.** Existing two-state `MergeState` maps: today's "committed" -> `Immutable`, "in-progress" -> `NotCommitted`. No existing recording has `CommittedProvisional`. Forward-compatible.
- **TerminalKind enum extension (`BGCrash`).** Added as new enum value. Legacy recordings with `Destroyed` + a BG-recording lineage migrate to `BGCrash` on first post-upgrade load. Migration is idempotent. Pre-feature BG-crashed siblings have `ParentBranchPoint.RewindPointId == null` (no RP was created), so they do NOT appear as Unfinished Flights despite matching other predicates. Accepted.
- **Ledger ActionId requirement.** Existing ledger actions without stable IDs get GUIDs generated on first post-upgrade load. Generation is deterministic per action content (hash of action fields) to avoid re-generation on re-load producing different IDs. After generation, IDs are immutable. Log `Info` summary: "Migrated <N> legacy actions to stable IDs".
- **LedgerTombstone.** New entity; empty on old saves.
- **ReFlySessionMarker.** Absent on old saves; nothing to migrate.
- **BranchPoint.RewindPointId.** Optional; absent on old BranchPoints. Renders null, treated as "no RP."
- **Recording schema additions.** All optional: `SupersededBy`, `Supersedes`, `SplitSnapshotFile`. Absent on old records.
- **Format version.** No bump required; all changes are additive.
- **Cross-save portability.** `saves/<save>/Parsek/RewindPoints/` and `_split.craft` files travel with the save directory.
- **Feature removal.** Orphan RP files and extra fields are safely ignorable.

---

## 10. Diagnostic Logging

Subsystem tags: `Rewind`, `RewindSave`, `Supersede`, `LedgerSwap`, `UnfinishedFlights`, `RewindUI`, `ERS`, `ELS`, `ReFlySession`.

### 10.1 RewindPoint lifecycle

- Create (multi-controllable): `Info` "[Rewind] Created RP id=<id> bp=<bpId> saveUT=<x> splitUT=<y> slots=<N> sessionProvisional=<bool> createdIn=<sessionId>"
- Skip (single controllable): `Verbose` "[Rewind] Skipped RP at bp=<bpId>: only <N> controllable"
- Deferred save queued: `Verbose` "[RewindSave] Queued save for rp=<id> deferMs=<t>"
- Save written: `Verbose` "[RewindSave] Wrote rp=<id> path=<path> size=<bytes>B elapsed=<ms>ms warpWas=<rate>"
- Save failed: `Warn` "[RewindSave] Failed rp=<id>: <reason>"
- Promote session-provisional -> persistent: `Verbose` "[Rewind] Promoted rp=<id> to persistent on session merge"
- Reap: `Verbose` "[Rewind] Reaped rp=<id>: no unfinished children remain"
- Purge on tree discard: `Info` "[Rewind] Purged <N> RPs on tree=<id> discard"
- Drift detected: `Verbose` "[Rewind] RP=<id>/BP=<bpId> linkage drift; BP wins"

### 10.2 Invocation

- Click: `Info` "[RewindUI] Rewind invoked recording=<recId> rp=<rpId>"
- Marker written: `Info` "[ReFlySession] Marker sessionId=<sid> treeId=<tId> activeRefly=<rId> originChild=<ocId> rp=<rpId>"
- Reconciliation bundle captured: `Verbose` "[Rewind] Bundle: recordings=<N> ledgerActions=<M> tombstones=<K> rps=<R> reservations=<Q>"
- Quicksave missing: `Warn` "[Rewind] Quicksave rp=<rpId> path=<path> unavailable. Marked corrupted."
- Quicksave loaded: `Verbose` "[Rewind] Loaded <path>"
- Reconciliation applied: `Verbose` "[Rewind] Reapplied <N>/<M>/<K>/<R>/<Q>"
- Strip + activate: `Info` "[Rewind] Stripped <N> siblings to ghosts, kept 1 live (vessel=<name> pid=<pid>), left <M> unrelated"
- Provisional recording created: `Info` "[Rewind] Created provisional rec=<rId> supersedeTarget=<ocId>"
- Sanity check (snapshot vs quicksave position): `Verbose` / `Warn` on drift "[Rewind] Drift vessel=<name> delta=<m>m, <ms>s"

### 10.3 Session suppression

- Session started: `Info` "[ReFlySession] Started. SuppressedSubtree=[<ids>]"
- Session ended: `Info` "[ReFlySession] Ended reason=<merge|discard|retry|fullRevert|loadInvalid>"

### 10.4 Supersede on merge

- Supersede commit: `Info` "[Supersede] rec=<newId> supersedes=<oldId> branchPoint=<bpId>"
- Tombstones created: `Info` "[LedgerSwap] <N> actions tombstoned scope=<subtreeDescription>"
- Contract action tombstones summary: `Info` "[LedgerSwap] Contracts: accepts=<a> completes=<c> fails=<f> cancels=<n>"
- Milestone monotonicity enforced: `Verbose` "[LedgerSwap] Preserved first-time flags: [<list>]"
- Reservation recompute: `Info` "[LedgerSwap] CrewReservation re-derived; kerbals returned to active: [<names>]"
- Recalc run: `Verbose` "[LedgerSwap] Recalculation: funds d=<x> sci d=<y> rep d=<z>"
- RP reap sweep: `Verbose` "[Rewind] Reap sweep: examined=<N> reaped=<M>"

### 10.5 Unfinished Flights membership

- Per-frame recompute (rate-limited shared key): `Verbose` "[UnfinishedFlights] <N> entries"
- Added: `Verbose` "[UnfinishedFlights] + rec=<id> branchPoint=<bpId>"
- Removed: `Verbose` "[UnfinishedFlights] - rec=<id> reason=<merged|superseded|treeDiscard>"
- Drag rejected: `Verbose` "[UnfinishedFlights] Rejected drag rec=<id> -> group=<gId>"

### 10.6 Revert dialog

- Shown: `Info` "[RewindUI] Revert-during-re-fly dialog shown"
- Retry: `Info` "[RewindUI] Retry; new sessionId=<sid>"
- Full revert: `Info` "[RewindUI] Full revert; tree=<id> discarded"
- Cancel: `Verbose` "[RewindUI] Cancelled"

### 10.7 Load-time sweep

- Marker validation pass: `Info` "[ReFlySession] Marker valid; sparing rec=<id>"
- Marker validation fail: `Warn` "[ReFlySession] Marker invalid reason=<field>; cleared"
- Zombie discarded: `Info` "[Rewind] Zombie rec=<id> discarded"
- Session-provisional RP purged: `Info` "[Rewind] RP=<id> purged on load (session <sid> not active)"
- Load sweep summary: `Info` "[Rewind] Load sweep: zombies=<N> provisionalRpsPurged=<M> orphanTombstones=<K> rpsLoaded=<L>"

### 10.8 Ledger tombstone mechanics

- Tombstone created: `Verbose` "[LedgerSwap] Tombstone <tId> retires action=<aId> by rec=<rId>"
- Orphan tombstone on load: `Warn` "[LedgerSwap] Orphan tombstone <tId>: supersededAction=<aId> not found"

---

## 11. Test Plan

Every test has a "what makes it fail" justification.

### 11.1 Unit tests

- **RewindPoint round-trip** incl. ChildSlots, IsSessionProvisional, CreatingSessionId. Fails if: any field loses on round-trip.
- **Recording round-trip** for MergeState tri-state, Supersedes/SupersededBy bidir, SplitTimeSnapshot reference.
- **ChildSlot.EffectiveRecordingId walk.** Table-driven: no supersede (returns origin); one supersede (returns new); two-level supersede (returns new2); corrupted chain (returns last visible).
- **Legacy MergeState migration.** Verify binary -> tri-state mapping.
- **Legacy TerminalKind migration.** Verify `Destroyed` with BG-recording lineage -> `BGCrash`. Idempotent.
- **Legacy ledger ActionId generation.** Verify deterministic, idempotent, unique.
- **IsUnfinishedFlight computed property** table-driven.
- **IsVisible computed property** trivial test.
- **ERS filter.** Given mixed recordings, verify correct filtering. Fails if: any subsystem reads from raw list.
- **ELS filter** with recording-scoped and tombstone-scoped exclusion. Both paths tested independently and in combination.
- **LedgerTombstone round-trip.**
- **ReFlySessionMarker round-trip + validation pass/fail table.**
- **Multi-controllable classifier.** Thresholds (0, 1, 2, 3+). EVA kerbals qualify.
- **First-time flag monotonicity.** Simulate set -> supersede of setter -> re-trigger. Flag never unsets.

### 11.2 Integration

- **Full supersede round-trip.** Build tree; commit AB'; assert SupersedesRecordingId/SupersededBy pair; assert tombstones created for each AB ledger action; assert recalculation excludes tombstoned actions; assert Unfinished Flights no longer lists AB.
- **Post-load vessel strip.** Stub the KSP load to return a known vessel list. Verify the strip removes non-selected children and leaves the selected one. Assert log counts match actual strip.
- **Nested RP session-provisional cleanup on discard.** Session-provisional RP + session-provisional BG children; discard session; assert all cleaned up.
- **Marker validation on load: all pass / all fail cases.** Table-driven.
- **Ledger tombstone scope: recording-level vs action-level.** Action scoped to superseded recording is excluded by recording-level filter even without an action-level tombstone. Action with tombstone but no recording scope is also excluded. Action with both is excluded once (not twice).
- **CrewReservationManager re-derivation** after supersede. Kerbal marked dead in old ledger, supersede retires death; reservation walker re-derives; kerbal is active.
- **Contract supersede by type.** Complete/Fail/Cancel/Accept each tested with independent ledger actions.
- **First-time milestone monotonicity across supersede.**
- **RP reap on last-child-resolved.** Two RPs with overlapping children; merge sequence reaps RP-1 and leaves RP-2.
- **Tree discard purges all Parsek files.** File listing before/after assertions.
- **Scenario save/load with all new entities round-trips.** Full tree + tombstones + marker + session-provisional RP.
- **F5 mid-re-fly, quit, load-via-persistent preserves re-fly.** Integration test with save lifecycle.
- **Deferred quicksave write + root-then-move.** Assert file lands in the Parsek/RewindPoints/ path and transient root-save file is gone.

### 11.3 Log assertion tests

- Every decision point in §10 has a log test.
- Session suppression logged once at session start, not per frame.
- ERS/ELS filter decisions do NOT log per-item unless in explicit debug mode.

### 11.4 Edge case tests

At least: 7.1, 7.2, 7.4 (partial snapshot failure), 7.5 (nested session guard), 7.6 (subtree suppression for BG descendants), 7.11 (session-provisional cleanup), 7.12 (F5 mid-re-fly load), 7.14 (contract Accept supersede), 7.15 (facility action supersede), 7.16 (kerbal death -> recovery), 7.17 (merging crashed re-fly), 7.30 (missing mod parts), 7.38 (unrelated mission guard), 7.40 (merge transaction rollback).

### 11.5 In-game tests (Ctrl+Shift+T)

- **CaptureRPOnStaging** - synthetic two-command-module decouple. Assert RP, snapshots, quicksave on disk, correct path.
- **SplitSnapshotVsClassifierFrame** - snapshot position within tolerance of classifier-frame vessel position. Fails if: snapshot captured at wrong moment.
- **InvokeRPStripAndActivate** - prepared save, invoke, assert strip counts, active vessel is selected child with quicksave-assigned PID (not a respawned one).
- **MergeReFlyCreatesSupersedeAndTombstones** - full run, assert new recording Immutable with Supersedes, old recording has SupersededBy, tombstones count matches old recording's ledger actions.
- **GhostSuppressionDuringReFly** - assert no ghost rendering for supersede-target during session (camera/visuals check).
- **LedgerSwapOnKerbalRecovery** - crewed booster, seed death, re-fly safe land, merge; assert rep total, reservation state.
- **UnfinishedFlightsRendering** - 0/1/3 entries, sort order, no-hide option.
- **WarpZeroedDuringSaveWrite** - split at warp 4x, assert warp 0 during save, warp restored after.
- **F5MidReFlyQuitResume** - F5 during re-fly, quit, load, assert re-fly resumes; marker validates; provisional recording not zombified.
- **MarkerInvalidationCleared** - corrupt the marker's ActiveReFlyRecordingId reference; load; assert marker cleared, zombie cleanup runs normally.

### 11.6 Performance

- RP quicksave write under 500ms (including deferred frame).
- Post-load strip under 100ms for 10 vessels.
- ERS / ELS compute under 5ms for 100 recordings / 10k ledger actions (cached; recomputed only on state change).
- Supersede commit + recalc under 200ms.

---

## 12. Implementation Sequencing

1. **Data model + legacy migration.** Fields, tri-state MergeState, ActionId generation, round-trip tests, legacy migrations.
2. **ERS/ELS filter implementation as a shared utility.** No subsystem rewiring yet; just the computed sets as a callable API. Tests.
3. **Wire subsystems to ERS/ELS one by one.** Ledger recalc first (test with stubbed tombstones). Then ghost walker (test with no session suppression). Then reservation manager. Each swap is a separate commit with regression tests.
4. **SplitTimeSnapshot capture** at multi-controllable splits. Classifier-frame hook. Test 11.5.2.
5. **RewindPoint creation + deferred quicksave write + root-then-move.** Test 11.5.1, 11.5.8.
6. **Unfinished Flights group (UI only, read-only).** Membership from ERS. Test 11.5.7.
7. **Rewind invocation + reconciliation + post-load strip + live vessel activation.** The largest phase. Split if needed into (a) marker write + load + reconciliation, (b) strip + activate.
8. **Provisional re-fly recording + SessionSuppressedSubtree wired into ghost walker + claim tracker.**
9. **Supersede commit + tombstone creation + contract action rules + reservation re-derivation + first-time monotonicity.** Tests 11.2 (most).
10. **RewindPoint reap on merge + tree discard purge.**
11. **Revert-to-Launch intercept dialog.**
12. **Load-time marker validation + zombie cleanup + session-provisional RP cleanup.**
13. **Polish: diagnostics disk usage, rename, row hide warning, split-snapshot pruning notes, doc updates.**

Phases 1-6 ship as "feature preview" (RPs captured, group visible, no invocation). Phase 7+ unlocks the feature.

---

## 13. Open Questions / Deferred

- **Re-entrancy testing with other mods' OnSave handlers.** The one-frame deferred save reduces risk but not to zero. Monitor during initial release with a "RewindSave deferred N ms" metric.
- **Split-snapshot pruning policy.** Snapshots for superseded recordings are retained indefinitely in v1. Investigate whether size becomes a problem in long careers.
- **Cross-recording stock-vessel visual divergence (§7.9).** Post-v1: consider freezing stock-vessel state at first Parsek-involved interaction.
- **Reservation conflict during re-fly (§7.39).** Current design allows overlap for the duration of the session. Post-v1: finer-grained reservation scoping if player reports confusion.
- **Multi-session re-flies.** v1 disallows (§7.5). Post-v1: consider stacked sessions with a stack-of-markers.
- **UI visual distinction between launch-R (timeline) and rewind-Unfinished-Flight Rewind.** Distinct icons and tooltips in the UI pass.
- **Physics easing for live-activated vessel at SaveUT.** Existing VesselSpawner behavior; verify no new easing regression.
- **Dialog copy polish.** "Merging a crashed re-fly seals this split," "Hiding an Unfinished Flight row makes Rewind hard to reach," "Starting this Rewind will discard your current mission," etc.
- **Content-hash deduplication** for simultaneous-split quicksaves (§7.19 dedup).
- **Auto-purge policy** for very-old reap-eligible RPs. None in v1; monitor via the disk-usage diagnostic display.
- **Cross-tree reservation registry.** Stations referenced across trees (§7.7). v1 ignores; post-v1 may need a bidirectional registry.

---

*End of design v0.3.*
