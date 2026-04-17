# Parsek - Rewind-to-Staging Design

*Design specification for Rewind Points on multi-controllable split events (staging, undocking, EVA) and the Unfinished Flights group that lets the player go back to a past split and control a sibling vessel they did not originally fly. Enables "fly the booster back" gameplay: launch AB, stage, fly B to orbit, merge, then rewind to the staging moment and fly A down as a self-landing booster.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to an immutable timeline, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder, timeline, and ledger systems with mid-mission rewind points anchored at multi-controllable-split BranchPoints.*

**Version:** 0.4 (revised after third-round internal + external reviews)
**Status:** Proposed.
**Depends on:** `parsek-flight-recorder-design.md` (recording DAG, BranchPoint, controller identity, ghost chains, additive-only invariant, ghost-until-chain-tip), `parsek-timeline-design.md` (rewind via quicksave, ledger replay, merge lifecycle), `parsek-game-actions-and-resources-recorder-design.md` (ledger model, immutable ActionId, reservations, career state replay).
**Out of scope:** Changes to ghost playback engine internals, merge dialog UI internals, orbital checkpointing, ledger recalculation engine internals, crew reservation manager internals. Debris splits (<2 controllable children) continue today's behavior with no rewind point.

---

## 1. Problem

Today, when the player stages and both halves are controllable (AB stack splits into booster A and upper stage B, each with a probe core or command pod), Parsek continues recording whichever vessel the player stays focused on. The other vessel is background-recorded until it crashes. If the player flies B to orbit and merges, A's BG-crash is the only record of what A did, and there is no in-game way to go back and fly A as a self-landing booster.

This design adds **Rewind Points** on splits that produce two or more controllable entities. A Rewind Point persists a KSP quicksave at split time plus a `PersistentId -> ChildSlot` table captured AT save time. Unmerged siblings (controllable BG-crashes whose parent BranchPoint has a RewindPoint) appear in a new **Unfinished Flights** group. Invoking one loads the quicksave, uses the saved PID table to strip sibling real vessels (replaced by ghost playback of their committed recordings), keeps the selected child alive from the quicksave's own copy, and starts a new provisional recording. On merge, the new recording commits additively alongside an append-only `RecordingSupersede(oldRec, newRec)` relation record; the old recording is never mutated. A narrow LedgerTombstone list retires specific reversible-effect ledger actions; sticky KSP-side career state (contract flags, milestones, facility upgrades, tech, strategies) is NOT reverted by supersede.

The feature is a natural extension of (a) the existing undock snapshot path, (b) the existing rewind-to-launch quicksave path, and (c) the additive-only tree invariant (flight-recorder principle 10). Three abstractions make it coherent:
- **Effective State Model (§3)** — one ERS/ELS rule for every subsystem that asks "what counts right now?"
- **Append-only supersede + tombstones** — no recording or action is ever mutated or deleted after commit.
- **Narrow v1 supersede scope** — re-fly is "physical replacement + kerbal recovery," not full career revision. Sticky KSP career flags survive supersede. Prevents the "retired but can't re-emit" problem that arises when we try to un-complete contracts/milestones that KSP has already set and won't re-fire.

---

## 2. Terminology

- **Split event**: BranchPoint of type `JointBreak`, `Undock`, or `EVA`.
- **Controllable entity**: a vessel with >=1 `ModuleCommand` part, OR an EVA kerbal. `IsTrackableVessel` covers both.
- **Multi-controllable split**: split event with >=2 controllable entities. Only these get a Rewind Point.
- **Rewind Point (RP)**: durable object at a multi-controllable split BranchPoint. Holds quicksave filename, save UT, ChildSlots, and PidSlotMap. Session-provisional during re-fly; persistent after session merge. See §5.1.
- **ChildSlot**: stable logical slot on an RP identifying one controllable output of the split. Immutable `OriginChildRecordingId` + derived `EffectiveRecordingId()` (see §5.2).
- **PidSlotMap**: `Dictionary<uint persistentId, int slotIndex>` captured on the RP AT quicksave-write time. Authoritative for the post-load strip. Maps each PID in the quicksave to the slot it represents.
- **Finished recording**: `MergeState == Immutable`.
- **Unfinished Flight**: matches `IsUnfinishedFlight` predicate (§3.1).
- **Re-fly session**: gameplay session after invoking an RP. Identified by `ReFlySessionId` (GUID) generated at invocation. Ends on merge, discard, retry (new session), or full-revert.
- **Supersede**: an append-only relation `RecordingSupersedeRelation { oldRec, newRec, UT }` stored in `ParsekScenario.RecordingSupersedes`. No recording is mutated; lookups filter via the list. See §5.3.
- **Ledger Tombstone**: append-only `LedgerTombstone { TombstoneId, SupersededActionId, RetiringRecordingId, UT }`. Keyed by immutable `ActionId`. Retires specific reversible-effect ledger actions on supersede.
- **ReFlySessionMarker**: singleton persisted struct in `PARSEK` scenario node while a re-fly session is active. Validated on load; invalid markers cleared. See §5.6.
- **ERS / ELS**: Effective Recording Set / Effective Ledger Set. See §3. All relevant subsystems consume these.
- **SessionSuppressedSubtree**: narrow forward-only closure from the supersede-target recording, stopping at mixed-parent BranchPoints. Applied during an active re-fly to a specific list of subsystems (ghost playback, claim tracker, map/tracking-station/CommNet presence).
- **Null-scoped action**: a ledger action with `RecordingId == null`. Examples: KSC spending, deadline-derived ContractFail, auto-generated kerbal stand-in events. These require action-level tombstones (recording-level filter can't reach them).

---

## 3. Effective State Model

Every subsystem that asks "what recordings count right now?" or "what ledger actions apply right now?" reads the same two sets. Without this, paradoxes emerge at the seams.

### 3.1 Effective Recording Set (ERS)

```
ERS = { r in RecordingStore.CommittedRecordings
      : r.IsVisible                                      (derived, see below)
        AND r.MergeState in {Immutable, CommittedProvisional} }

IsVisible(r)             := NOT exists rel in ParsekScenario.RecordingSupersedes
                                  : rel.oldRec == r.Id

IsUnfinishedFlight(r)    := r in ERS
                            AND r.MergeState == CommittedProvisional
                            AND r.TerminalKind == BGCrash
                            AND r.ParentBranchPoint?.RewindPointId != null
                            AND r.IsControllable
```

`IsVisible` is a **derived lookup over the append-only supersede relation list**. The Recording itself has no `SupersededByRecordingId` field. (v0.3 had such a field; the "set field on old recording at supersede time" was the one remaining additive-invariant violation. v0.4 fixes this.)

### 3.2 Effective Ledger Set (ELS)

```
ELS = { a in Ledger.Actions
      : a.ActionId is present (immutable, stable; generated on load for
                               legacy actions - see §9)
        AND (a.RecordingId == null
             OR a.RecordingId identifies a recording in ERS)
        AND NOT exists t in Ledger.Tombstones
                       : t.SupersededActionId == a.ActionId }
```

Two filter layers both must pass:
- **Recording-level**: action is scoped to a recording that is in ERS.
- **Action-level tombstone**: action has NO matching tombstone.

Null-scoped actions (§2) pass the recording filter trivially; tombstone-level filtering is their only retirement path. §6.6 merge therefore scans ALL null-scoped actions for applicability to the supersede scope and generates tombstones for any that are supersede-retirable (e.g., a deadline-derived `ContractFail` whose contract-target-vessel was the supersede subtree).

**However, per §6.6.4 v1 rules, most null-scoped actions are NOT retired on supersede.** Only specific categories (listed in §6.6.4) are tombstone-eligible. This is the narrow supersede scope decision.

### 3.3 Session-suppressed subtree (narrow carve-out)

During an active re-fly, a carve-out applies to a specific set of subsystems only. NOT to career-state subsystems.

**Closure rule (forward-only, merge-guarded):**

```
SessionSuppressedSubtree(refly) = closure(refly.OriginChildRecordingId)

def closure(r):
    result = { r }
    for each child c in forward-children-of(r):                # child direction only
        if c.ParentRecordingIds subset-of-or-equal-to result.Ids:
            # c's only parent is inside the subtree; include it recursively
            result.add_all(closure(c))
        else:
            # c has a parent outside the subtree (dock/board merge);
            # stop descending — c is NOT in the session suppression
    return result
```

Forward-only direction (parent -> child) prevents suppression leaking upward through the DAG. The mixed-parent stop-condition prevents suppression leaking sideways through dock/board merges where a superseded branch joined with an unrelated branch.

**Consumed by:**
- Ghost playback walker
- Vessel-identity claim / chain-tip tracker
- GhostMapPresence (map view + tracking station + CommNet relay visibility)
- WatchModeController (camera-follow ghost selection)

**NOT consumed by** (these read ERS/ELS directly, no re-fly carve-out):
- Ledger recalculation engine (funds, science, rep totals)
- Career state UI
- CrewReservationManager (BUT see §3.3.1 kerbal-dual-residence carve-out)
- Contract state derivation (the stock KSP contract system - see §6.6.4)
- Milestone flags (sticky in KSP's ProgressTracking)
- Timeline view
- Resource recalculation
- Unfinished Flights group
- RewindPoint reap logic
- FacilityUpgrade/Strategy/Tech state derivation

### 3.3.1 Kerbal dual-residence carve-out

A second narrow carve-out applies ONLY to `CrewReservationManager` for kerbals physically embodied in the live re-fly vessel (present in `FlightGlobals.ActiveVessel.GetVesselCrew()` at any point during the session). These kerbals are exempted from reservation-lock for the duration of the session; otherwise the player may silently lose EVA access or crew-transfer options because the reservation manager thinks the kerbal is dead/retired per the BG-crash ledger event.

This is the minimum carve-out needed so the player can fly the re-fly at all. Other reservation effects (kerbals reserved to OTHER still-ghost recordings, stand-in generation for unreserved slots) remain active.

### 3.4 Subsystem consumer table

| Subsystem | Reads | Writes |
|---|---|---|
| Ghost playback walker | ERS \ SessionSuppressedSubtree | never writes |
| Vessel-identity claim / chain-tip tracker | ERS \ SessionSuppressedSubtree | never writes |
| GhostMapPresence | ERS \ SessionSuppressedSubtree | ProtoVessel lifecycle |
| WatchModeController | ERS \ SessionSuppressedSubtree | camera anchoring |
| CrewReservationManager | ERS for walk; live-re-fly crew exempted | reservations |
| Ledger recalculation engine | ELS | derived career state |
| Career-state UI (funds/science/rep) | ELS-derived totals | never |
| Contract state | (KSP's sticky state, not Parsek-driven for supersede) | — |
| Milestone flags | (KSP's sticky ProgressTracking) | — |
| Timeline view | ERS lifecycle events + ELS actions | never |
| Resource recalculation | ELS (resource-changing actions) | never |
| Unfinished Flights | ERS filtered by IsUnfinishedFlight | never |
| RewindPoint reap | ERS children of RP ChildSlots | RP removal only |

**Grep-audit requirement (§11).** No code outside this table walks `RecordingStore.CommittedRecordings` or `Ledger.Actions` directly. Implementation must include a CI-level grep that fails on raw access from any file not in this list. The existing code surface (GhostMapPresence, tracking station, watch mode, diagnostics, UI surfaces) is known to have raw walkers today; all must be converted to ERS/ELS readers as a prerequisite phase for this feature.

### 3.5 Invariants

1. **Append-only across the board.** No recording, ledger action, supersede relation, or tombstone is ever mutated or deleted after write. Tree-discard is the only removal path, and it deletes complete subtrees as one atomic sweep.
2. **Supersede is a relation, not a field.** `RecordingSupersedeRelation` lives in its own persistent list. `IsVisible` is derived. No field on a committed Recording is ever written post-commit.
3. **Subtree supersede.** When a re-fly supersedes OriginChild, the merge transaction also adds `RecordingSupersedeRelation` records for every descendant in OriginChild's forward subtree (BG-recorded orphan debris, BG-recorded sub-breaks, etc. — per the closure rule in §3.3). The whole subtree disappears from ERS together.
4. **First-time flags are monotonic and KSP-owned.** KSP's `ProgressTracking` holds first-time achievement flags (First Orbit, First Landing, etc.). Parsek does NOT try to un-set flags on supersede; they are sticky. The ledger's "first-time" reward actions are also NOT tombstone-eligible in v1 supersede scope.
5. **Narrow v1 supersede effects.** See §6.6.4. Only a specific list of ledger action types is tombstone-eligible. Contract completions/failures, milestones, facility state, strategies, tech all survive supersede.
6. **ERS and ELS are computed, not stored.** They are derived from their source collections + the supersede relation list + the tombstone list. Caching is an implementation detail; the semantic definitions are not cache-dependent.

---

## 4. Mental Model

### 4.1 Tree evolution across a full flight + re-fly + nested-refly cycle

`(V)` = in ERS (visible, not superseded). `(H)` = hidden (has a RecordingSupersedeRelation marking it superseded).

Launch:
```
[session, nothing committed yet]
ABC (NotCommitted, live, slot=root)   (V)
```

Staging (split-1): ABC -> AB + C. Both controllable. RP-1 created session-provisional, 2 slots.
```
ABC (NotCommitted, terminated at split-1)   (V)
 |
 +-- [slot-1a: AB]  (NotCommitted, BG)        (V)
 +-- [slot-1b: C]   (NotCommitted, live)      (V)
     [RP-1 (session-provisional, pidMap={pid_AB:1a, pid_C:1b})]
```

Flying C; AB BG-crashes.
```
ABC (NotCommitted)                           (V)
 |
 +-- AB  (NotCommitted, BGCrash)               (V)
 +-- C   (NotCommitted, at orbit)              (V)
     [RP-1 session-provisional]
```

Session merge.
```
ABC  (Immutable)                              (V)
 |
 +-- AB  (CommittedProvisional, BGCrash)       (V)  Unfinished Flight
 +-- C   (Immutable, orbit)                    (V)
     [RP-1 PROMOTED to persistent]
     Unfinished Flights = {AB}
```

Player invokes RP-1, picks AB.
- **Phase 1 of marker lifecycle:** load quicksave, run reconciliation, strip, activate selected child, create provisional re-fly recording AB'. `AB'.MergeState = NotCommitted`, `AB'.CreatingSessionId = <new session>`, `AB'.SupersedeTargetId = AB.Id` (intent; NOT a committed supersede relation).
- **Phase 2:** only now, write the `ReFlySessionMarker` to scenario, naming `AB'.Id` as `ActiveReFlyRecordingId`.

```
ABC  (Immutable)                              (V)
 |
 +-- AB   (CommittedProvisional, BGCrash)     (V)  supersede-target, in SessionSuppressedSubtree
 +-- AB'  (NotCommitted, live,
           SupersedeTargetId=AB, CSId=sess1)  (V)  provisional re-fly
 +-- C    (Immutable)                          (V)
     [RP-1; SessionSuppressedSubtree = {AB}]
     ReFlySessionMarker { SessionId=sess1, ActiveReFlyRecordingId=AB', OriginChildRecordingId=AB, ... }
     Unfinished Flights = {AB}  (unchanged; AB' is NotCommitted so not in ERS)
```

During re-fly: ERS = {ABC, AB, AB', C}. Ghost walker enumerates ERS \ {AB} = {ABC, AB', C} (but AB' is active vessel, filtered further by active-vessel rule; ABC and C play as ghosts). Career state reads ERS/ELS directly including AB's rep penalty.

AB' stages into A+B. RP-2 session-provisional.
```
ABC  (Immutable)                              (V)
 |
 +-- AB   (CommittedProvisional, BGCrash)     (V)
 +-- AB'  (NotCommitted, terminated at split-2) (V)
 |    |
 |    +-- [slot-2a: A]  (NotCommitted, BG, CSId=sess1)  (V)
 |    +-- [slot-2b: B]  (NotCommitted, live, CSId=sess1) (V)
 |         [RP-2 (session-provisional, CSId=sess1)]
 +-- C    (Immutable)                          (V)
     [RP-1, RP-2, marker for sess1]
```

Land B, player merges.

On merge:
1. AB' committed Immutable, `SupersedeTargetId -> AB` lifted into a `RecordingSupersedeRelation { oldRec=AB, newRec=AB', UT=now }` appended to `ParsekScenario.RecordingSupersedes`.
2. Subtree supersede: no BG-recorded descendants of AB exist in this example. In general: for every descendant of OriginChild reached by the forward-only closure, append another supersede relation pointing to AB'.
3. Tombstones: scan AB's subtree ledger actions for tombstone-eligible types (§6.6.4); emit LedgerTombstones. Scan null-scoped actions in the same subtree scope; emit tombstones as applicable.
4. Promote RP-2 session-provisional -> persistent. Clear its `CreatingSessionId`.
5. RP reap sweep (see §6.6.8) — reaps RP-1 since AB is now superseded and C is Immutable.
6. Clear `ReFlySessionMarker`.
7. Recalculation runs from ELS.

```
ABC   (Immutable)                             (V)
 |
 +-- AB  (CommittedProvisional, SUPERSEDED by AB')  (H)
 +-- AB' (Immutable)                          (V)
 |    |
 |    +-- A (CommittedProvisional, BGCrash)    (V)  new Unfinished Flight
 |    +-- B (Immutable, Landed)                (V)
 |         [RP-2 persistent]
 +-- C   (Immutable)                           (V)
     [RP-1 REAPED; RecordingSupersedes=[{AB,AB'}]; Tombstones=[...]]
     Unfinished Flights = {A}
```

### 4.2 Key invariants (recap)

1. Tree never shrinks. Supersede is a relation; old recordings stay on disk.
2. RP lifetime: session-provisional while session is active; persistent on merge; reap-eligible when no ChildSlot.EffectiveRecording is an Unfinished Flight.
3. Career state reads ELS regardless of active re-fly (with narrow §3.3.1 exception for live-crew kerbals).
4. Ghost-visible systems apply SessionSuppressedSubtree during active re-fly.
5. Supersede in v1 is narrow: physical replacement + kerbal recovery. Sticky KSP state (contract completions/failures, milestone flags, facility, strategy, tech) is NOT reverted; see §6.6.4 tombstone scope.

---

## 5. Data Model

### 5.1 RewindPoint

```
RewindPoint
    string  Id                              "rp_" + Guid
    string  BranchPointId                   anchor (authoritative)
    double  SaveUT                          Planetarium.UT at the quicksave write
    string  QuicksaveFilename               under saves/<save>/Parsek/RewindPoints/
    List<ChildSlot>  ChildSlots             one per controllable entity at split
    Dictionary<uint,int>  PidSlotMap        KSP persistentId -> SlotIndex at save time
                                            (authoritative for post-load strip; §6.4)
    bool    IsSessionProvisional
    string  CreatingSessionId               if session-provisional, the session GUID
    DateTime CreatedRealTime                debug
    bool    Corrupted                       set true on quicksave validation failure
```

`PidSlotMap` is captured on the deferred frame after `GamePersistence.SaveGame` returns. It reads `FlightGlobals.Vessels` and, for each vessel that corresponds to a ChildSlot, records the PID-to-slot mapping. The strip (§6.4) is authoritative from this map, not from a global "identify which recording owns this vessel" lookup that could mis-match when KSP inherits the parent stack's PID into one of the split outputs.

### 5.2 ChildSlot

```
ChildSlot
    int     SlotIndex                      0-based, stable
    string  OriginChildRecordingId         immutable; the recording born at split for this slot

Derived:
    EffectiveRecordingId()                 walks FORWARD via RecordingSupersedeRelation,
                                           NOT via any field on the Recording. Stops at
                                           the latest non-superseded id in the chain.
                                           Tree-scoped: walk halts if a supersede relation
                                           crosses a tree boundary (v1 guard; cross-tree
                                           supersede is not a v1 concept).
```

Walk direction: from origin forward. Given `oldRec`, find `rel in ParsekScenario.RecordingSupersedes where rel.oldRec == oldRec`; if found, recurse on `rel.newRec`; else return `oldRec`. Cycle detection: maintain a visited set; if revisited, log `Error` and return the visited leaf (should be impossible by append-only invariant but cheap to guard against).

### 5.3 RecordingSupersedeRelation (new persistent list)

```
RecordingSupersedeRelation
    string  RelationId                     "rsr_" + Guid
    string  OldRecordingId                 the superseded recording (stays in RecordingStore)
    string  NewRecordingId                 the superseding recording
    double  UT                             UT at which the merge occurred
    DateTime CreatedRealTime               debug
```

Persisted in `ParsekScenario.RecordingSupersedes`. Append-only. Never mutated. Removed only on tree-discard.

### 5.4 BranchPoint additions

```
string RewindPointId                       null if <2 controllable children; else non-null
```

Bidirectional with `RewindPoint.BranchPointId`. Authoritative: `BranchPoint.RewindPointId`.

### 5.5 Recording additions

v0.4 REMOVES the `SupersededByRecordingId` field from v0.3. Visibility and supersede chain are now derived from the `RecordingSupersedes` list.

```
enum MergeState                            NotCommitted / CommittedProvisional / Immutable

string CreatingSessionId                   null for recordings created outside any re-fly
                                           session; set to the session's GUID for recordings
                                           created during a re-fly (including nested BG-recorded
                                           children). Used for load-time spare-set logic (§6.9).

string SupersedeTargetId                   transient, set only on NotCommitted provisional
                                           re-fly recordings before merge. Indicates intent.
                                           Cleared and not persisted after merge (merge
                                           converts intent into a RecordingSupersedeRelation).
                                           NOT a form of mutation on old recordings.
```

Computed helpers (derived from `RecordingSupersedes` list, not from fields):
```
IsVisible(r)                 := NOT exists rel : rel.OldRecordingId == r.Id
IsSuperseded(r)              := NOT IsVisible(r)
SupersedingChain(r)          := walk forward via RecordingSupersedes
IsControllable(r)             := has ModuleCommand parts OR is an EVA kerbal
```

### 5.6 Ledger additions

```
Every LedgerAction requires stable immutable ActionId (schema precondition per existing
game-actions design; v1 adds enforcement and legacy migration - see §9).

LedgerTombstone
    string  TombstoneId                    "tomb_" + Guid
    string  SupersededActionId             immutable ActionId
    string  RetiringRecordingId            the NEW recording whose merge caused the tombstone
    double  UT                             UT of the merge
    DateTime CreatedRealTime               debug
```

Append-only. Multiple tombstones for the same ActionId are tolerated (rare; e.g., a supersede chain). Filter is "at least one tombstone exists."

### 5.7 ReFlySessionMarker

```
ReFlySessionMarker (singleton; present in PARSEK scenario node iff a re-fly is active)
    string  SessionId                      unique GUID per invocation/retry
    string  TreeId                         RecordingTree this re-fly belongs to
    string  ActiveReFlyRecordingId         the NotCommitted provisional re-fly recording
    string  OriginChildRecordingId         supersede target
    string  RewindPointId                  invoked RP
    double  InvokedUT                      UT at invocation
    DateTime InvokedRealTime               wall-clock
```

**Lifecycle (two-phase write; v0.4 fix for external review #3):**

1. Rewind invocation: load quicksave, strip, activate, **create** the provisional re-fly recording AB' with a fresh `Id` and `CreatingSessionId = newSessionId`. AB' is in the RecordingStore.
2. ONLY THEN write the `ReFlySessionMarker` to `ParsekScenario` naming `AB'.Id` as `ActiveReFlyRecordingId`.

Any serialization (F5, autosave) that lands BETWEEN phase 1 and phase 2 finds the recording but no marker. On load without marker, the recording (being `NotCommitted` under an RP-anchored BranchPoint) would normally be zombied. To close this window: phase 1's recording creation is deferred by one physics frame from the reconciliation load; phase 2 happens synchronously with phase 1's frame. The window is a single frame and non-observable to autosave mechanics.

A secondary mitigation: a FORWARD-REFERENCED marker is ALLOWED. If the marker references an `ActiveReFlyRecordingId` that doesn't yet exist in RecordingStore at load time, and the marker's `InvokedUT` matches the quicksave's UT within tolerance, the load proceeds and creates the provisional re-fly recording from the RP's stored ChildSlot on-load. In practice v1 treats this path as a pure safety net and logs `Warn` if it triggers.

**Cleared** on: re-fly merge (recording transitions to Immutable), re-fly discard (RTSC or quit-without-merge), retry (new SessionId written, not cleared), full-revert, load-time validation failure.

**Validation on load** (§6.9): all six fields consistent with current scenario state. On any fail, marker cleared + `Warn` logged; zombie cleanup proceeds.

### 5.8 ParsekScenario persistence additions

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
            creatingSessionId =
            corrupted = False
            createdRealTime = 2026-04-17T21:35:12Z
            PID_SLOT_MAP
                ENTRY pid = 12345678 slotIndex = 0
                ENTRY pid = 87654321 slotIndex = 1
            CHILD_SLOT
                index = 0
                originChildRecordingId = rec_01234
            CHILD_SLOT
                index = 1
                originChildRecordingId = rec_01235
        POINT ...
    RECORDING_SUPERSEDES
        REL
            id = rsr_abcd
            oldRec = rec_01234
            newRec = rec_03333
            ut = 1742800.00
            createdRealTime = 2026-04-17T22:10:00Z
        REL ...
    LEDGER_TOMBSTONES
        TOMBSTONE
            id = tomb_bcd1
            supersededActionId = act_09876
            retiringRecordingId = rec_03333
            ut = 1742800.00
        TOMBSTONE ...
    REFLY_SESSION_MARKER (optional, present only during active session)
        sessionId = rf_c3d4
        treeId = tree_mission17
        activeReFlyRecordingId = rec_03333
        originChildRecordingId = rec_01234
        rewindPointId = rp_a1b2c3d4
        invokedUT = 1742538.43
        invokedRealTime = 2026-04-17T21:38:00Z
```

All entities are optional in schema; absence parses as empty.

**Recording schema additions:**
```
RECORDING
    ...
    mergeState = CommittedProvisional
    creatingSessionId =                        # empty for pre-feature or outside-session
    supersedeTargetId =                        # empty in persisted form (transient only)
```

`SupersedeTargetId` is written only while the recording is `NotCommitted`; on transition to `Immutable` at merge it is cleared (intent is now committed as a supersede relation). On load, any `Immutable` recording with a non-empty `SupersedeTargetId` is logged `Warn` and the field is treated as cleared.

### 5.9 Directory layout

```
saves/<save>/
    persistent.sfs                             (KSP standard)
    Parsek/
        Recordings/
            <recordingId>.prec                 (trajectory)
            <recordingId>_vessel.craft         (ghost-conversion snapshot; existing)
            <recordingId>_ghost.craft          (ghost mesh; existing)
        RewindPoints/
            <rewindPointId>.sfs                (NEW; KSP-format quicksave)
```

v0.3 specified `<recordingId>_split.craft` (SplitTimeSnapshot). **v0.4 drops SplitTimeSnapshot** from v1. External review's second-order concern #3 and internal review's A2: the field had ambiguous purpose, no defined recovery path, and would add serialization/disk cost without a concrete consumer. If a post-v1 recovery-fallback feature wants it, add it back.

### 5.10 Unfinished Flights UI group

Virtual node in `GroupHierarchyStore`. Membership derived per frame from ERS filtered by `IsUnfinishedFlight`. Not manually editable. Drag-into rejected. Cannot be hidden (no hide option). Sort by parent-mission MET ascending.

---

## 6. Behavior

### 6.1 At a multi-controllable split

Triggered from `ParsekFlight.DeferredJointBreakCheck`, `DeferredUndockBranch`, or the EVA handler after the classifier identifies >=2 controllable entities.

Sequence:
1. The existing flight-recorder pipeline creates each child Recording. Their `Id` values are stable from creation forward.
2. Construct `RewindPoint` with ChildSlots. Empty `PidSlotMap` for now.
3. `IsSessionProvisional` and `CreatingSessionId` set from current `ReFlySessionMarker` state (if any).
4. **Defer the quicksave write one frame** (coroutine; lands on the next FixedUpdate).
5. On the deferred frame:
   - **Scene guard**: if `HighLogic.LoadedScene != GameScenes.FLIGHT`, abort RP creation, delete any pre-created state, log `Warn`. (§7.3 edge case.)
   - Force `TimeWarp.SetRate(0, instant:true)` if rate > 1. Restore after save.
   - Call `GamePersistence.SaveGame(<tempName>, "persistent", SaveMode.OVERWRITE)`. **KSP writes to the root save dir.**
   - Atomically move the written file to `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs` via `FileIOUtils.SafeMove` (tmp+rename).
   - `SaveUT = Planetarium.GetUniversalTime()` at the save call.
   - **Populate `PidSlotMap`:** for each ChildSlot, look up the controllable vessel `v` whose `OriginChildRecordingId` matches the slot; record `PidSlotMap[v.persistentId] = slot.SlotIndex`. If lookup fails for any slot, log `Warn` and leave that slot unmapped (§7.X edge case).
   - Write `BranchPoint.RewindPointId`. Verify round-trip.
   - Append RP to `ParsekScenario.RewindPoints`.
6. Log `Info` summary.

**Failure modes:**
- Save throws / disk full / permissions: RP not created. BranchPoint.RewindPointId stays null. Split proceeds today's path. Log `Warn`.
- Scene guard triggers: same as above. Log `Warn` "[RewindSave] Deferred save aborted: scene=<loaded>".

### 6.2 Between split and merge

Focused vessel records normally. Unfocused controllables BG-recorded. All recordings `NotCommitted`.

On session merge:
- Focused vessel's recording -> `Immutable`.
- BG-recorded controllable children whose parent BranchPoint has `RewindPointId != null` -> `CommittedProvisional`.
- Session-provisional RPs promote to persistent.
- Unfinished Flights recomputes from ERS.

### 6.3 Invoking a Rewind Point

UI preconditions:
- `RewindPoint.Corrupted == false`.
- Quicksave file exists and deep-parses (PartLoader can resolve all referenced parts - §7.X mod-parts-missing).
- No other re-fly session is currently active (§7.5 — guard with dialog).

Confirmation dialog (conceptual):
> Rewind to stage separation of <ParentVesselName> at UT <formatted MET>?
> <ChildVesselName> spawns live at the split moment. <N> previously-merged siblings play as ghosts. Career state during this attempt still shows the previous crash's penalties; they retire only when you commit the new flight.
> Only specific career effects are reversible - see the wiki for details.
> Cancel or Proceed.

On Proceed:
1. `SessionId = Guid.NewGuid()`.
2. Capture reconciliation bundle (§6.4 table).
3. Load the RP's quicksave (existing rewind-to-quicksave path, substituted filename).
4. Post-load, on first scene-ready frame:
   - Apply reconciliation (restore in-memory state from bundle).
   - Run post-load strip (§6.4 step 4).
   - Activate selected child's vessel (§6.4 step 5).
   - **Create provisional re-fly Recording AB'** with fresh Id, `MergeState=NotCommitted`, `CreatingSessionId=SessionId`, `SupersedeTargetId=<selectedChild.Id>`. Add to RecordingStore. (Phase 1 of marker lifecycle.)
   - **Write `ReFlySessionMarker`** naming AB'.Id. (Phase 2.)
   - Run `LedgerOrchestrator.Recalculate()` to catch career state up.
5. Log `Info` summary.

### 6.4 Reconciliation + post-load strip + activation

**Reconciliation bundle** (captured pre-load, reapplied post-OnLoad):

| Domain | Post-load source | Preserved from in-memory |
|---|---|---|
| Planetarium.UT | Quicksave = SaveUT | — |
| KSP raw funds/science/rep | Quicksave | caught up by ELS recalc at end |
| KSP vessels | Quicksave | — (strip + activate in step 4/5) |
| `RecordingStore` | Discarded | Preserved (full history including superseded) |
| `Ledger.Actions` | Discarded | Preserved |
| `Ledger.Tombstones` | Discarded | Preserved |
| `ParsekScenario.RewindPoints` | Discarded | Preserved |
| `ParsekScenario.RecordingSupersedes` | Discarded | Preserved |
| `CrewReservationManager` | Discarded | Preserved (recomputed post-apply) |
| `RecordingTree.PreTreeFunds/Science` baselines | Quicksave | — |
| `GroupHierarchyStore` | Discarded | Preserved |
| `MilestoneStore` (legacy) | Discarded | Preserved |
| `ReFlySessionMarker` | — | Written new in §6.3 step 4 (phase 2) |

**Step 4: post-load strip (explicit matching rule).**

For each vessel `v in FlightGlobals.Vessels`:

1. **Ghost ProtoVessel guard.** If `v.persistentId in GhostMapPresence.ghostMapVesselPids`, skip this vessel. (It's a ghost map presence we spawned; the strip does not touch it.)
2. **Authoritative slot lookup.** If `RewindPoint.PidSlotMap.TryGetValue(v.persistentId, out slotIdx)`:
   - If `slotIdx == selectedSlotIdx`: this is the live re-fly target. Keep as active-vessel candidate for step 5.
   - Else: this is a non-selected sibling. Remove from physics via existing ghost-conversion path. Register its Recording for ghost playback.
3. **Fallback matching by root-part PID.** If PidSlotMap doesn't contain `v.persistentId` but `v` has a root-part PID matching any ChildSlot's initial root-part PID (recorded at split time in a separate compact table), use that match. This covers rare cases where KSP re-PIDs the vessel between save and load.
4. **Else, leave alone.** The vessel does not belong to this RP's slot set. It is either a pre-existing stock vessel, a vessel from a different RecordingTree, or debris. DO NOT strip it. (v0.3's broad "matches any ERS recording" rule would have mis-stripped unrelated committed real vessels at their chain tips.)

Log `Info` "[Rewind] Strip: slots=[<stripped slot indices>] kept=<selected slot> leftAlone=<N> ghostsGuarded=<M>".

**Sanity check (not abort):** after step 5, verify active vessel's position/velocity is consistent with where the RP expected. Large discrepancy logs `Warn` but doesn't abort.

**Step 5: activate selected child's vessel.** `FlightGlobals.SetActiveVessel(selectedVessel)`. PID unchanged. Selected vessel's situation, resources, crew are as of the quicksave (SaveUT). Crew list becomes the canonical live crew for kerbal dual-residence resolution (§3.3.1).

**Step 6: create provisional re-fly Recording.** See §6.3 step 4.

### 6.5 Within a re-fly session

Ghost walker and related subsystems (§3.4 table) enumerate `ERS \ SessionSuppressedSubtree`. Ledger and other career subsystems enumerate ERS/ELS directly.

SessionSuppressedSubtree follows the forward-only merge-guarded closure rule (§3.3). Multi-parent descendants where a parent is outside the subtree halt the descent.

Nested multi-controllable splits create session-provisional RPs with `CreatingSessionId = activeSession.SessionId`. Child recordings at the nested split also carry `CreatingSessionId = activeSession.SessionId`. On merge, both promote to persistent; on discard, both purge.

### 6.6 Merging a re-fly session (staged commit)

Merge is journaled per §7.40 (below) to handle partial-write / exception recovery.

Steps (in order; each step durable-save-clean before next; critical irreversible step 10 happens LAST):

1. **Journal begin.** Write a journal entry `ParsekScenario.MergeJournal` (new field) stating the in-flight merge: `{ReFlySessionId, Phase="Begin", Timestamp}`.

2. **Recordings commit.** For each `NotCommitted` recording in the session:
   - If it's the provisional re-fly AB': `MergeState = Immutable`. Clear `SupersedeTargetId`.
   - If it's a BG-recorded nested child under a split with `RewindPointId != null`: `MergeState = CommittedProvisional`.
   - Else: `MergeState = Immutable`.

3. **Supersede relation commit.** Append `RecordingSupersedeRelation { OldRecordingId = selectedChild.Id, NewRecordingId = AB'.Id }` to `RecordingSupersedes`. For each descendant `d` in `ForwardMergeGuardedSubtree(selectedChild)` (same closure as SessionSuppressedSubtree): append `{OldRecordingId = d.Id, NewRecordingId = AB'.Id}`. (Subtree supersede - external review #1.)

4. **LedgerTombstones for reversible-effect actions.** Scan the ledger for actions `a` where:
   - `a.RecordingId` is in the superseded subtree (step 3), OR
   - `a.RecordingId == null` AND `a.Type` is null-scoped-eligible (see below) AND `a` is clearly attributable to the superseded subtree by payload matching.

   For each such `a`, check `a.Type` against the **v1 tombstone-eligible type list**:
   - `KerbalDeath` - yes
   - `ReputationEarning` / `ReputationPenalty` scoped to a tombstoned `KerbalDeath` (by bundled payload) - yes
   - **All other types - NO.** Contract Accept/Complete/Fail/Cancel, MilestoneAchievement, FacilityUpgrade/Destruction/Repair, StrategyActivate/Deactivate, TechResearch, ScienceEarning, FundsEarning/Spending stay ELS-effective.

   Emit `LedgerTombstone` records for the eligible actions. Log `Info` "[LedgerSwap] Tombstoned <N> actions (KerbalDeath=<d> rep-bundled=<r>); all other career effects sticky".

5. **Narrow v1 effects recap (reinforcement).** The external review's issue #2 (contract-events-can't-re-emit) is addressed by this narrow scope: we do NOT attempt to un-complete/un-fail contracts or un-set milestone flags. Those are KSP's sticky ProgressTracking state; Parsek's supersede does not touch them. The re-fly's own legitimately-fired new contract/milestone events (if any) are added to the ledger additively as usual.

6. **CrewReservationManager recomputes** from ERS (now excluding the superseded subtree via `IsVisible`). Kerbals whose death event was tombstoned return to active via the normal reservation walk - no special swap logic.

7. **Promote session-provisional RPs to persistent.** For each RP with `IsSessionProvisional == true AND CreatingSessionId == activeSession.SessionId`: set `IsSessionProvisional = false`, clear `CreatingSessionId`.

8. **Durable save (first safe point).** Run ScenarioModule OnSave. Fsync the save file. All in-memory state from steps 1-7 is now durable on disk. If any prior step threw, rollback (see §7.40) and abort: journal entry stays; on next load, it's noticed and the in-flight merge is either retried or cleanly discarded.

9. **RP reap check (post-durable).** For each persistent RP: walk ChildSlots, compute EffectiveRecordingId, check if any is an Unfinished Flight. If none are, RP is reap-eligible. List them.

10. **Irreversible file ops (only after step 8's durable save).** For each reap-eligible RP: delete its quicksave file. Remove the RP from scenario. Log `Verbose`.

11. **Clear `ReFlySessionMarker`.** Remove from scenario.

12. **Second durable save.** Persists the reaped-RP state + cleared marker.

13. **Clear `MergeJournal`.** Set to empty. Third durable save.

14. **Run `LedgerOrchestrator.Recalculate()`** to propagate career state.

15. Log `Info` summary.

**Failure recovery:**
- Exception between step 1 and step 8: in-memory state may be partially mutated. Rollback to the state BEFORE step 1 by reloading the scenario from disk. `MergeJournal` on-disk says "Begin"; recognize this on the next merge attempt and discard the in-memory attempt, let the player retry.
- Exception between step 8 and step 10: in-memory state is durable, but irreversible file ops may have partially run. The journal says "Begin". On-disk state is "post-merge durable; RPs may be half-reaped." On next load, scan for RPs whose quicksave is missing but whose record still exists; those are half-reaped, finish the reap. Scan for recordings whose supersede relation exists but are NotCommitted (shouldn't happen if step 2 was durable, but check). This recovery is handled by a post-load consistency pass (§6.9 step 7).
- Exception after step 10: minor inconvenience; journal gets stale. §6.9's post-load scan clears stale journal entries.

### 6.7 Revert-to-Launch during re-fly

Standard Revert-to-Launch is intercepted. 3-option dialog:
- **Retry**: discard current provisional, generate new `SessionId`, update marker (NOT clear), reload same quicksave, new provisional.
- **Full Revert**: clear marker, invoke tree-discard flow. All under-tree RPs and recordings deleted.
- **Cancel**: resume.

### 6.8 Session end without merge

- Return to Space Center: clear marker, discard provisional, purge session-provisional RPs.
- Game quit without Space Center: marker stays in save. Load-time validation decides (§6.9).

### 6.9 Load-time sweep (gather-then-delete, single pass)

1. KSP loads save; ScenarioModule OnLoad populates all state.
2. **Journal check.** If `MergeJournal.Phase == "Begin"` and marker is cleared, the merge was durable but cleanup didn't complete. Run finisher: reap half-reaped RPs (quicksave-missing detection), clear journal.
3. **Marker validation.** If present:
   - Verify `ActiveReFlyRecordingId` exists AND `MergeState == NotCommitted`.
   - Verify `OriginChildRecordingId` is in ERS AND `CommittedProvisional` AND `BGCrash` AND `ParentBranchPoint.RewindPointId != null`.
   - Verify `RewindPointId` exists AND is persistent-or-session-provisional AND `CreatingSessionId == SessionId` (for session-provisional).
   - Verify `TreeId` matches origin's tree.
   - On all-pass: `markerValid = true`.
   - On any fail: `markerValid = false`, clear marker, log `Warn`.
4. **Gather spare set.** If `markerValid`:
   - All recordings with `CreatingSessionId == marker.SessionId` are spared.
   - All RPs with `CreatingSessionId == marker.SessionId` are spared.
   - (This includes nested NotCommitted children created during the session, not just `ActiveReFlyRecordingId`. External review #4 fix.)
5. **Gather discard set.**
   - Recordings with `MergeState == NotCommitted` AND `ParentBranchPoint?.RewindPointId != null` AND not in spare set.
   - RPs with `IsSessionProvisional == true` AND not in spare set.
6. **Delete as one pass.** For each to-discard recording: delete sidecar files, remove from RecordingStore. For each to-discard RP: delete quicksave file, remove from scenario.
7. **Supersede consistency pass.** For each `RecordingSupersedeRelation`: verify both `OldRecordingId` and `NewRecordingId` resolve to recordings in RecordingStore. Orphan relations (rare; only possible from tree-discard-during-merge) logged `Warn` and removed.
8. **Orphan tombstone check.** For each `LedgerTombstone`: verify `SupersededActionId` exists. Orphans logged `Warn`, kept.
9. **Marker-valid forward-reference safety net.** If marker was valid but `ActiveReFlyRecordingId` resolution would have failed WERE IT NOT FOR this step: the recording might not yet exist if the save was taken in the marker-write window. Check the RP: if RP has `CreatingSessionId == marker.SessionId` AND the quicksave is at `marker.InvokedUT`, create the provisional recording on the fly. Log `Warn` - this is a rescue path. (§5.6 phase-1/phase-2 window mitigation.)
10. `LedgerOrchestrator.Recalculate()` from ELS.
11. Log `Info` summary.

### 6.10 Tree discard

Revert-to-Launch (or full-revert dialog): tree discard.
- Remove all recordings under the tree.
- Reap all RPs under the tree (delete quicksaves).
- Remove all supersede relations whose OldRecordingId or NewRecordingId is in the tree.
- Remove all ledger actions scoped to tree recordings. Remove related tombstones.
- CrewReservationManager recomputes.
- Clear marker.

Existing tree-discard sweep extended with the two new persistent lists (`RecordingSupersedes`, `LedgerTombstones`) and the two new files (`RewindPoints/<id>.sfs`).

---

## 7. Edge Cases

Each: scenario / expected / v1 verdict.

### 7.1 Single-controllable split
No RP. Debris path unchanged. Handled in v1.

### 7.2 3+ controllable children at one split
One RP with N ChildSlots and N PidSlotMap entries. Player iterates. Handled in v1.

### 7.3 Scene transition during deferred save
Deferred worker guards `LoadedScene == FLIGHT`; else abort RP creation, log Warn. (External review E3.) Handled in v1.

### 7.4 Partial snapshot failure at multi-child split
Missing PidSlotMap entry for one slot -> that slot's Rewind button disabled with tooltip. RP still created if at least 2 slots mapped. Log Warn.
Handled in v1.

### 7.5 Invoking RP while a re-fly session is active
UI guard + dialog. No nested sessions in v1. Handled in v1.

### 7.6 Re-fly target has BG-recorded descendants
SessionSuppressedSubtree includes descendants via forward-only closure. Merge step 3 emits supersede relations for each descendant. (External #1 fix.) Handled in v1.

### 7.7 Pre-existing Parsek-committed vessel from a different tree (cross-tree dock)
Station S is an Immutable from M0 tree. Live AB' physically docks with S. Dock event added to AB' recording normally. No cross-tree merge. Handled in v1.

### 7.8 Pre-existing non-Parsek stock vessel
Standard interaction. Dock event in AB' recording. No paradox. Handled in v1.

### 7.9 Visual divergence for cross-recording stock-vessel interaction
Ghost-B recorded dock with station S at UT=X. AB' also docks with S at UT=X-100. S is moved by live AB'. At UT=X ghost-B's playback visually tries to dock with S, which is elsewhere. Accepted v1 visual limitation.

### 7.10 EVA duplicate on load
Strip detects kerbal in EVA form AND in source vessel crew. Canonical live location per §3.3.1: selected child's crew manifest at load time. Remove duplicate from source vessel crew if not selected. (External review E1.) Handled in v1.

### 7.11 Nested RP session-provisional cleanup on parent re-fly discard
`CreatingSessionId` on nested RP = discarded-session's id. Load-sweep spare-set logic (§6.9) removes it. Handled in v1.

### 7.12 F5 mid-re-fly, quit, load-via-persistent/load-via-F5
Session marker validated. Recordings with matching `CreatingSessionId` spared. Re-fly resumes. (Internal + external review merge of probes.) Handled in v1.

### 7.13 Autosave between phase-1 (recording created) and phase-2 (marker write)
Window is one frame; nominally unobservable. Safety net in §6.9 step 9 creates the provisional recording on-load if the marker forward-references a missing `ActiveReFlyRecordingId` but the RP quicksave UT matches. Log Warn.
Handled in v1.

### 7.14 Supersede target had contract Accept/Complete/Fail/Cancel
v1 does NOT tombstone these. They stay in ELS. Contract state remains per KSP's sticky ProgressTracking. External review #2 fix by narrowing scope: we never attempt to un-emit-and-re-emit.
Handled in v1 with documented limitation.

### 7.15 Supersede target had facility/strategy/tech action
v1 does NOT tombstone these. Sticky in KSP. Handled in v1 with documented limitation.

### 7.16 Kerbal death + recovery via supersede
`KerbalDeath` action tombstoned. Bundled rep penalty tombstoned. Reservation walker re-derives from ERS. Kerbal returns to active. (v1 tombstone-eligible type.) Handled in v1.

### 7.17 Re-fly crashes; player merges
Re-fly Immutable, Crashed. Supersedes original BG-crash. Merge dialog warning:
> Merging a crashed flight replaces the original crash on the timeline. The supersede's rep penalties (kerbal deaths if any) will apply instead of the original's. Other career effects from the original (contracts, milestones, facility changes) remain as-is.
Player CAN subsequently re-fly again - the new crash is a new Unfinished Flight (satisfying `IsUnfinishedFlight` predicate). v0.3's dialog copy saying "You will not be able to Rewind this split again" was WRONG; v0.4 corrects.
Handled in v1.

### 7.18 Re-fly reaches stable outcome, player discards
Provisional discarded. Marker cleared. Original BG-crash visible. RP durable. Handled in v1.

### 7.19 Simultaneous multi-controllable splits in one frame
Two independent RPs. Two quicksave writes (serial through KSP). Handled in v1.

### 7.20 F9 during re-fly
Existing F9 + auto-resume. Marker in the F9 save determines behavior. Handled in v1.

### 7.21 Warp during re-fly
Standard Parsek warp. Handled in v1.

### 7.22 Rewind click during scene transition
UI guard. Handled in v1.

### 7.23 Two children crash at same location
Independent rewinds. Handled in v1.

### 7.24 Re-fly vessel with no engines
Allowed. Handled in v1.

### 7.25 Drag Unfinished Flight into manual group
Rejected. Handled in v1.

### 7.26 Mods modifying joint-break / destruction
Classifier is ModuleCommand-based. Handled in v1.

### 7.27 High physics warp at split
Forced to 0 during save, restored after. Handled in v1.

### 7.28 Disk usage
Settings > Diagnostics shows total RP disk usage. No auto-purge. Handled in v1.

### 7.29 Mod-part missing on re-fly load
Deep-parse precondition in §6.3 detects before load. RP marked corrupted. Dialog. Handled in v1.

### 7.30 Cannot hide Unfinished Flights group
No hide option. Collapsible only. Handled in v1.

### 7.31 Non-crashed BG sibling
Terminal = Landed (BG-determined). `IsUnfinishedFlight` false. Sits at CommittedProvisional. Accepted v1 limitation.

### 7.32 First-time milestone earned by superseded recording
KSP's ProgressTracking flag stays set. v1 does NOT tombstone milestone actions. No un-set. Handled in v1 with documented limitation.

### 7.33 Classifier drift across versions
Old RPs retained. No retroactive reclassify. Log Verbose on load. Handled in v1.

### 7.34 Rename / hide on Unfinished Flight row
Rename: persists to Recording. Hide row: allowed with tooltip warning. Handled in v1.

### 7.35 Corrupted quicksave at invocation
RP marked Corrupted. Rewind button disabled. Handled in v1.

### 7.36 Auto-save concurrent with RP save
One-frame defer + root-then-move. Serial through KSP. Handled in v1.

### 7.37 Rewind while flying unrelated new mission
Guard dialog. Provisional M2 is tree-discarded then M1 rewind proceeds. Handled in v1.

### 7.38 Pre-existing vessel reservation conflict during re-fly
Kerbal Jeb reserved by station S per original timeline. Live AB' has Jeb onboard. §3.3.1 carve-out: kerbals physically embodied in live re-fly vessel are exempted from reservation-lock for the session. Handled in v1.

### 7.39 Merge transaction interruption
Journaled staged commit. Step 8 is first durable point. Exceptions before step 8 rollback via scenario reload. Exceptions between 8 and 10 leave a "post-durable with pending reaps" state; §6.9 step 2 finisher handles on next load. (External review #8.) Handled in v1.

### 7.40 Strip encounters a ghost ProtoVessel already in FlightGlobals.Vessels
`GhostMapPresence.ghostMapVesselPids` guard in §6.4 step 1 skips these. (External review missing case E2.) Handled in v1.

### 7.41 Strip encounters an unrelated committed real vessel at chain tip
Vessel's PID is NOT in the active RP's PidSlotMap -> left alone per §6.4 step 4. v0.3's broad "match any ERS recording" rule would have mis-stripped. (External review #5 fix + missing case E3.) Handled in v1.

### 7.42 Session-suppressed subtree with mixed-parent descendant
Descendant c has `ParentRecordingIds = {supersedeTarget, otherTreeVessel}`. §3.3 closure halts at c (doesn't include it). c remains visible and plays normally. (External review missing case E4.) Handled in v1.

### 7.43 Supersede scope includes null-scoped synthetic action (deadline ContractFail)
v1 does NOT tombstone ContractFail. Deadline-derived fails stay. Rep penalty stays. Player accepts. (External review missing case E5 and v1's narrow scope.) Handled in v1 with documented limitation.

### 7.44 Contract double-completion attempt
BG-crash completed contract X (ELS has ContractComplete). Player re-flies and would complete X again. KSP's ProgressTracking has X set - won't re-fire. No new ContractComplete action emitted. v1 behavior: player gets the OLD completion's rewards (already in ELS, sticky). Re-fly doesn't add a duplicate. Merge doesn't tombstone the original (per §6.6.4 scope). Net: contract is complete, player got rep once. Handled in v1 with documented behavior.

### 7.45 Re-fly also fails; player merges the crashed re-fly
Re-fly becomes new Unfinished Flight (TerminalKind=BGCrash, MergeState=CommittedProvisional, parent BP has RewindPointId). Player can re-fly again. §7.17 dialog copy updated accordingly. (Internal review S3 fix.) Handled in v1.

### 7.46 EffectiveRecordingId walk direction
Walks `RecordingSupersedes` forward from `OldRecordingId` (not backward). Cycle detection maintains visited set. Tree-scoped (v1 guard against cross-tree supersede which is not a concept). (Internal review S4 + external probe 8.) Handled in v1.

### 7.47 KSC action during active re-fly
Player doesn't leave flight during re-fly, so KSC actions don't fire mid-session. If they somehow do (mod scenarios), they're added to the ledger with null RecordingId and not in any supersede scope; they stay in ELS. Handled in v1.

### 7.48 MergeJournal stale after clean exit
Journal remains "empty" most of the time. §6.9 step 2 finisher handles stale "Begin" states on load. Handled in v1.

---

## 8. What Doesn't Change

- **Flight-recorder principle 10** (additive-only). Enforced by: supersede is a relation record (not a field), tombstones are append-only, all writes to committed recordings are forbidden (load-time check in §6.9 step 3 flags any stray `SupersedeTargetId` on an Immutable recording).
- **Single-controllable split path.** Unchanged.
- **Merge dialog UI.** Still lists session recordings. Supersede decisions at commit time.
- **Ghost playback engine.** Reads ERS via walker; SessionSuppressedSubtree is an upstream filter.
- **Active-vessel ghost suppression.** Natural consequence of ERS filter.
- **Ledger recalc engine.** Reads ELS; no engine changes.
- **Rewind-to-launch.** Unchanged path.
- **Loop / overlap / chain.** Read ERS.
- **Recording file format.** All additions (`<id>_split.craft` from v0.3 is DROPPED).
- **Reservation manager internals.** Re-derivation from ERS is existing.
- **KSP ProgressTracking.** Sticky by design. v1 supersede does NOT touch it.

---

## 9. Backward Compatibility

- **Old saves without RPs.** Load cleanly. All new lists empty. Unfinished Flights empty.
- **Binary -> tri-state MergeState migration.** "committed" -> Immutable; "in-progress" -> NotCommitted. No legacy CommittedProvisional.
- **TerminalKind extension (BGCrash).** Legacy `Destroyed` with BG-recording lineage migrates to `BGCrash` on first post-upgrade load. Idempotent. Pre-feature BG-crashed siblings do NOT appear as Unfinished Flights (parent BP has no RP). Accepted.
- **Ledger ActionId migration.** Existing actions without stable IDs get deterministic hash-based IDs on first post-upgrade load. Log "Migrated <N> legacy actions to stable IDs." Idempotent.
- **Recording schema additions.** `CreatingSessionId`, `SupersedeTargetId` optional. `SupersededByRecordingId` (v0.3 field) is REMOVED from schema; if present in old v0.3-schema saves (none should exist in production since v0.3 never shipped), ignored on load + logged Warn.
- **New persistent lists** (`RecordingSupersedes`, `LedgerTombstones`, `MergeJournal`, optional `REFLY_SESSION_MARKER`): all optional, empty on old saves.
- **Format version.** No bump; additive only.
- **Cross-save portability.** `saves/<save>/Parsek/RewindPoints/` travels with the save.
- **Feature removal.** Orphan RP files + extra fields are ignorable. Old saves can't directly un-supersede but can be tree-discarded.

---

## 10. Diagnostic Logging

Tags: `Rewind`, `RewindSave`, `Supersede`, `LedgerSwap`, `UnfinishedFlights`, `RewindUI`, `ERS`, `ELS`, `ReFlySession`, `MergeJournal`.

### 10.1 RP lifecycle
- Create: `Info` "[Rewind] RP id=<id> bp=<bpId> saveUT=<x> splitUT=<y> slots=<N> sessionProv=<bool> creatingSess=<sid>"
- Skip (single controllable): `Verbose` "[Rewind] Skipped RP bp=<bpId>: <N> controllable"
- PidSlotMap built: `Verbose` "[Rewind] RP=<id> PidSlotMap: <N> entries"
- Scene-guard abort: `Warn` "[RewindSave] Scene guard aborted deferred save for rp=<id>: scene=<loaded>"
- Save written: `Verbose` "[RewindSave] Wrote rp=<id> path=<path> size=<bytes>B ms=<t> warpWas=<r>"
- Save failed: `Warn` "[RewindSave] Failed rp=<id>: <reason>"
- Promote session-provisional -> persistent: `Verbose` "[Rewind] Promoted rp=<id>"
- Reap: `Verbose` "[Rewind] Reaped rp=<id>"
- Tree discard: `Info` "[Rewind] Purged <N> RPs on tree=<id> discard"

### 10.2 Invocation
- Click: `Info` "[RewindUI] Invoked rec=<id> rp=<rpId>"
- Marker Phase 1 (recording created): `Info` "[ReFlySession] Phase1 created provisional rec=<id> sess=<sid>"
- Marker Phase 2 (marker written): `Info` "[ReFlySession] Phase2 marker sess=<sid> tree=<tId> activeRefly=<rId> origin=<ocId> rp=<rpId>"
- Strip: `Info` "[Rewind] Strip slotsRemoved=[<indices>] keptSlot=<idx> leftAlone=<N> ghostsGuarded=<M>"
- Sanity drift: `Warn` "[Rewind] Post-activate drift vessel=<name> delta=<m>m"

### 10.3 Session suppression
- Session start: `Info` "[ReFlySession] Start. Suppressed=[<ids>]"
- Session end: `Info` "[ReFlySession] End reason=<merge|discard|retry|fullRevert|loadInvalid>"

### 10.4 Supersede / LedgerSwap
- Supersede relation: `Info` "[Supersede] rel=<id> old=<oldId> new=<newId>"
- Subtree supersede: `Info` "[Supersede] Added <N> descendant supersede relations for subtree of <origId>"
- Tombstones: `Info` "[LedgerSwap] Tombstoned <N> actions; KerbalDeath=<d> repBundle=<r>; all other types sticky per v1 scope"
- Skipped non-eligible action types: `Verbose` "[LedgerSwap] Skipped <N> actions from supersede scope (type-ineligible: <breakdown>)"
- Reservation recompute: `Info` "[LedgerSwap] CrewReservation re-derived; kerbals returned to active: [<names>]"
- Recalc totals: `Verbose` "[LedgerSwap] Recalc: funds d=<x> sci d=<y> rep d=<z>"

### 10.5 Unfinished Flights
- Rate-limited: `Verbose` "[UnfinishedFlights] <N> entries"
- +: `Verbose` "[UnfinishedFlights] + rec=<id> bp=<bpId>"
- -: `Verbose` "[UnfinishedFlights] - rec=<id> reason=<merged|superseded|treeDiscard>"
- Drag reject: `Verbose` "[UnfinishedFlights] Rejected drag rec=<id>"

### 10.6 Revert dialog
- Shown: `Info` "[RewindUI] Revert-during-re-fly dialog"
- Retry: `Info` "[RewindUI] Retry; new sess=<sid>"
- Full revert: `Info` "[RewindUI] Full revert; tree=<id> discarded"
- Cancel: `Verbose` "[RewindUI] Cancelled"

### 10.7 Load sweep
- Journal finisher: `Info` "[MergeJournal] Finisher: resumed half-reap of <N> RPs"
- Marker validation pass: `Info` "[ReFlySession] Marker valid; spare set = <N> records"
- Marker validation fail: `Warn` "[ReFlySession] Marker invalid field=<f>; cleared"
- Forward-reference rescue: `Warn` "[ReFlySession] Marker refers to missing recording; creating on-fly from RP sess=<sid> rp=<rpId>"
- Zombie discarded: `Info` "[Rewind] Zombie rec=<id> discarded"
- Session-prov RP purged: `Info` "[Rewind] RP=<id> purged (session <sid> inactive)"
- Supersede consistency: `Verbose` "[Supersede] Consistency pass: <N> relations valid, <M> orphans"
- Orphan tombstone: `Warn` "[LedgerSwap] Orphan tombstone <tId>"
- Load summary: `Info` "[Rewind] Load sweep: zombies=<N> provRPs=<M> orphans=<K> rpsLoaded=<L> superseseded=<S>"

### 10.8 MergeJournal
- Begin: `Info` "[MergeJournal] sess=<sid> phase=Begin"
- Durable point: `Info` "[MergeJournal] sess=<sid> phase=Durable"
- Cleared: `Verbose` "[MergeJournal] sess=<sid> cleared"
- Stale on load: `Warn` "[MergeJournal] Stale Begin entry for sess=<sid>; investigating"

---

## 11. Test Plan

### 11.1 Unit

- RewindPoint round-trip (incl. PidSlotMap, ChildSlots, session-provisional state).
- Recording round-trip (MergeState tri-state, CreatingSessionId, SupersedeTargetId transient-only persisted-value handling).
- RecordingSupersedeRelation round-trip.
- LedgerTombstone round-trip.
- ReFlySessionMarker round-trip + validation table (all 4 pass/fail axes).
- MergeJournal round-trip.
- Legacy MergeState / TerminalKind / ActionId migration (idempotent, deterministic).
- IsVisible(r) lookup: test with 0, 1, chain-of-3 supersede relations.
- IsUnfinishedFlight predicate table-driven.
- EffectiveRecordingId walk direction (forward via supersedes), cycle detection, tree-scope halt.
- ERS filter completeness.
- ELS filter: recording-level AND action-level.
- v1 tombstone-eligible type classifier.
- SessionSuppressedSubtree closure: forward-only, merge-guard halt.

### 11.2 Integration

- Full supersede round-trip: BG-crash AB, re-fly AB' merged. Assert RecordingSupersedes has the relation; AB not mutated; IsVisible(AB)==false; ERS excludes AB; ELS excludes eligible AB actions; ELS keeps non-eligible AB actions.
- Subtree supersede: BG-crash AB with BG-recorded orphan debris d; supersede AB with AB'; assert d also gets a supersede relation to AB'.
- Mixed-parent halt: BG-recorded descendant c with `ParentRecordingIds={AB, externalRec}`; closure does NOT include c; assert ERS still includes c.
- KerbalDeath tombstone round-trip + reservation re-derive.
- Contract sticky: BG-crash completed a contract; re-fly merged; assert ContractComplete action NOT tombstoned; assert contract still complete in KSP ProgressTracking; assert ELS includes the action.
- Null-scoped action handling: deadline-derived ContractFail; not v1-tombstone-eligible; stays in ELS across supersede.
- RP reap on last-child-resolved.
- Tree discard purges all new persistent lists + quicksave files.
- Scenario save/load round-trip with all new entities.
- F5 mid-re-fly -> quit -> load: marker validates, all session-tagged recordings spared, re-fly resumes.
- F5 during marker-write window: forward-reference rescue creates recording on-load; Warn logged.
- Post-load strip: authoritative match via PidSlotMap; ghost ProtoVessel guard; fallback to root-part PID; unrelated vessels left alone.
- Merge journal crash-recovery: inject exception at step 4 (before durable); reload; in-memory state discarded; journal stale; finisher clears.
- Merge journal crash-recovery: inject exception at step 9 (between durable and irreversible ops); reload; finisher completes reap.
- EffectiveRecordingId walks forward; v0.3 direction bug regression-proof.

### 11.3 Log assertion

- Every decision point in §10 has a log-present test.
- Session-suppression logged once per session, not per frame.
- Merge "all other types sticky" summary logged.

### 11.4 Edge-case tests

At least: 7.3 (scene guard), 7.4 (partial slot), 7.6 (subtree), 7.11 (nested cleanup), 7.12 (F5 resume), 7.13 (phase window rescue), 7.14 (contract sticky), 7.16 (kerbal recovery), 7.17 (merged crash re-rewindable), 7.40 (ghost ProtoVessel guard), 7.41 (unrelated vessel left alone), 7.42 (mixed-parent halt), 7.39 (journal crash-recovery), 7.46 (walk direction).

### 11.5 In-game (Ctrl+Shift+T)

- **CaptureRPOnStaging** - synthetic 2-command-module decouple. Assert RP, PidSlotMap populated, quicksave in Parsek/RewindPoints/.
- **SavePathRootThenMove** - assert no stray save file in save root after RP creation.
- **InvokeRPStripAndActivate** - assert strip counts, ghost-guard count, active vessel PID unchanged from quicksave.
- **MergeReFlyCreatesSupersedeAndTombstones** - assert relation in RecordingSupersedes, no mutation of old recording fields, tombstones for KerbalDeath only.
- **ContractCompletionStickyAcrossSupersede** - BG-crash completes contract, re-fly merges, assert contract still complete, rep unchanged.
- **GhostSuppressionDuringReFly** - no ghost rendering for supersede-target subtree.
- **KerbalRecoveryOnSupersede** - crewed BG-crash + safe-land re-fly; kerbal active, reservation re-derived, rep delta applied.
- **UnfinishedFlightsRenderingAndNoHide** - sort, collapse, hide-disabled.
- **WarpZeroedDuringSave** - warp 4x at split; save taken at 0x; 4x restored.
- **F5MidReFlyResume** - F5, quit, load; re-fly resumes; marker validates; all session recordings spared.
- **ForwardReferenceRescue** - marker references a recording not in store; rescue path logs Warn and creates the recording; re-fly resumes.
- **MergeInterruptionRecovery** - inject exception at step 4 and 9; verify expected recovery behavior.
- **MergedCrashRewindable** - §7.17 case: merged crash re-fly is a new Unfinished Flight.

### 11.6 Performance

- Quicksave write <500ms.
- Strip <100ms for 10 vessels.
- ERS/ELS compute <5ms for 100 recordings / 10k actions (cached; recomputed only on state change).
- Supersede merge + recalc <200ms.

### 11.7 Grep-audit (CI gate)

- Static check: no file outside the §3.4 consumer table directly references `RecordingStore.CommittedRecordings` or `Ledger.Actions` or iterates them. Existing raw-access code surfaces (GhostMapPresence, tracking station, watch mode, diagnostics, UI) must be converted to ERS/ELS consumers as a prerequisite phase.
- Rg pattern: `\.(CommittedRecordings|Actions)\b` outside the approved file list fails CI.

---

## 12. Implementation Sequencing

1. **Data model + legacy migration.** All new fields, tri-state MergeState, ActionId deterministic generation. Round-trip tests.
2. **ERS/ELS shared utility.** Computed-on-demand API. Cache invalidation hooks. Tests.
3. **Grep-audit phase.** Convert every raw `CommittedRecordings`/`Actions` consumer (GhostMapPresence, watch mode, diagnostics, UI) to ERS/ELS. CI gate added. Full regression run; nothing breaks because supersede list is empty in baseline.
4. **Split-time RewindPoint creation** with PidSlotMap + scene guard + warp-to-0 + root-save-then-move.
5. **Unfinished Flights UI (read-only).** Membership from ERS. No-hide.
6. **Rewind invocation Phase 1** (load + strip + activate + provisional recording).
7. **Rewind invocation Phase 2** (marker write + forward-reference rescue path).
8. **SessionSuppressedSubtree** wiring into ghost walker + claim tracker + GhostMapPresence + WatchModeController.
9. **Merge: supersede relations + subtree closure.** Commits the relation list. `IsVisible` lookups flip. No ledger changes yet.
10. **Merge: v1 tombstone-eligible scope + LedgerTombstones.** Kerbal death + bundled rep only.
11. **Merge: staged commit / journal / durable fences.** Crash-recovery tests.
12. **RP reap (post-durable) + tree discard purge.**
13. **Revert-to-Launch intercept dialog.**
14. **Load-time sweep** (journal finisher, marker validation, spare-set, zombie cleanup, consistency pass).
15. **Polish.** Diagnostics disk display, rename/hide warnings, merge-dialog copy, logs.

Phases 1-5 ship as "feature preview" (no gameplay change; RPs captured, group visible, rewind button disabled). Phase 6+ unlocks the feature progressively.

---

## 13. Open Questions / Deferred

- **OnSave re-entrancy** with other mods' save handlers. One-frame defer reduces risk; monitor with telemetry.
- **Snapshot recovery (v2)**: reinstating SplitTimeSnapshot as a quicksave-corrupt fallback needs a concrete "synthesize world from committed ghosts + current state + snapshot" mechanism. Deferred.
- **Broader supersede scope (v2)**: beyond KerbalDeath, some ledger action types (e.g., vessel-destruction-rep) might be safe to tombstone without hitting KSP's sticky ProgressTracking. Explore case-by-case in v2.
- **Cross-recording stock-vessel visual divergence.** Post-v1: freeze stock-vessel state at first Parsek-involved interaction.
- **Cross-tree dock recording bidirectional registry.**
- **Nested re-fly sessions.** v1 bans (§7.5). v2 could support with a stack of markers.
- **Auto-purge policies for very-old reap-eligible RPs.** None in v1; monitor via disk-usage.
- **Merge-dialog copy polish.** "Merging a crashed re-fly replaces the original crash", "Hiding an Unfinished Flight row makes Rewind hard to reach", "Starting this Rewind will discard your current mission", "v1 supersede limits: contract/milestone flags stay set; see wiki for details".
- **Physics easing** when activating from quicksave at SaveUT. Existing VesselSpawner behavior; verify no regression.
- **Quicksave content-hash dedup** for simultaneous splits (§7.19).
- **First-time flag registry as Parsek-owned** (if v2 broadens supersede scope such that we want to un-set). Currently flagged as KSP-owned ProgressTracking; sticky.

---

*End of design v0.4.*
