# Parsek - Rewind-to-Separation Design

*Design specification for Rewind Points on multi-controllable split events (staging, undocking, EVA) and the Unfinished Flights group that lets the player go back to a past split and control a sibling vessel they did not originally fly. Enables "fly the booster back" gameplay: launch AB, stage, fly B to orbit, merge, then rewind to the staging moment and fly A down as a self-landing booster.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to an immutable timeline, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder, timeline, and ledger systems with mid-mission rewind points.*

**Version:** 0.5 (revised after fourth review)
**Status:** Proposed.
**Target release:** Parsek v0.9 (next minor after the v0.8.x line).
**Depends on:** `parsek-flight-recorder-design.md` (recording DAG, BranchPoint, controller identity, ghost chains, additive-only invariant, ghost-until-chain-tip), `parsek-timeline-design.md` (rewind via quicksave, ledger replay, merge lifecycle), `parsek-game-actions-and-resources-recorder-design.md` (ledger model, immutable ActionId, reservations, career state replay).
**Out of scope:** Changes to ghost playback engine internals, merge dialog UI internals, orbital checkpointing, ledger recalculation engine internals, crew reservation manager internals. Debris splits (<2 controllable children) continue today's behavior with no rewind point.

---

## 1. Problem

Today, when the player stages and both halves are controllable (AB stack splits into booster A and upper stage B, each with a probe core or command pod), Parsek continues recording whichever vessel the player stays focused on. The other vessel is background-recorded until it crashes. If the player flies B to orbit and merges, A's BG-crash is the only record of what A did, and there is no in-game way to go back and fly A as a self-landing booster.

This design adds **Rewind Points** on splits that produce two or more controllable entities. A Rewind Point persists a KSP quicksave at split time plus a `PersistentId -> ChildSlot` table captured AT save time. Unmerged siblings (controllable BG-crashes whose parent BranchPoint has a RewindPoint) appear in a new **Unfinished Flights** group. Invoking one loads the quicksave, uses the saved PID table to strip sibling real vessels (replaced by ghost playback of their committed recordings), keeps the selected child alive from the quicksave's own copy, and starts a new provisional recording. On merge, the new recording commits additively alongside an append-only `RecordingSupersede(oldRec, newRec)` relation record; the old recording is never mutated.

### 1.1 What supersede is (and isn't) in v1

**v1 supersede is narrow: physical replay + kerbal-death correction.**

- **What IS replaced on supersede:** the trajectory shown for this split output (ghost playback uses the superseding recording, not the superseded one); vessel-identity claims and chain-tip physical presence (the new recording takes the slot); kerbal-death ledger events attributable to the superseded subtree (and the rep penalties bundled with them) — via explicit `LedgerTombstone` records. Kerbals thought dead return to active via the normal reservation walk.

- **What is NOT replaced on supersede:** contract state (Accept / Complete / Fail / Cancel), milestone flags, facility upgrades/destruction/repair, strategies, tech research, non-kerbal-death reputation deltas, funds and science earnings/spending. These are "sticky" in their respective KSP subsystems — `ContractSystem` for contracts, `ProgressTracking` for milestone/first-time flags, `ScenarioUpgradeableFacilities` for facilities, `StrategySystem` for strategies, `ResearchAndDevelopment` for tech, and so on. KSP will not re-emit a contract completion after the `ContractSystem` marks it complete, so tombstoning the event and hoping the re-fly emits a fresh one produces zero credit. v1 sidesteps this by leaving all non-kerbal-death ledger actions in `ELS`, regardless of whether their source recording was superseded.

This narrow framing is the central design decision. Earlier drafts implied a general "effective state" abstraction that treated supersede as a single mechanism for both visual and career replacement; the fourth review surfaced that this was structurally impossible to deliver because KSP does not cooperate on re-emitting retired events. The fix is to make ledger retirement explicitly opt-in per action type (only `KerbalDeath` + bundled rep in v1) rather than an automatic consequence of recording-level supersede.

The rest of the doc consistently carries this framing: supersede is a **physical-visibility / claim-tracker mechanism**, not a ledger or career-state eraser.

---

## 2. Terminology

- **Split event**: BranchPoint of type `JointBreak`, `Undock`, or `EVA`.
- **Controllable entity**: a vessel with >=1 `ModuleCommand` part, OR an EVA kerbal. `IsTrackableVessel` covers both.
- **Multi-controllable split**: split event with >=2 controllable entities. Only these get a Rewind Point.
- **Rewind Point (RP)**: durable object at a multi-controllable split BranchPoint. Holds quicksave filename, save UT, ChildSlots, PidSlotMap, RootPartPidMap. Session-provisional during re-fly; persistent after session merge.
- **ChildSlot**: stable logical slot on an RP identifying one controllable output of the split. Immutable `OriginChildRecordingId` + derived `EffectiveRecordingId()`.
- **PidSlotMap**: `Dictionary<uint vesselPersistentId, int slotIndex>` captured on the RP at quicksave-write time. Authoritative primary match for the post-load strip.
- **RootPartPidMap**: `Dictionary<uint rootPartPersistentId, int slotIndex>` captured at the same time. Fallback match when KSP re-PIDs a vessel between save and load.
- **Finished recording**: `MergeState == Immutable`.
- **Unfinished Flight**: matches `IsUnfinishedFlight` predicate (§3.1).
- **Re-fly session**: gameplay session after invoking an RP. Identified by `ReFlySessionId` (GUID) generated at invocation. Ends on merge, discard, retry (new session), or full-revert.
- **Supersede**: an append-only relation `RecordingSupersedeRelation { oldRec, newRec, UT }` in `ParsekScenario.RecordingSupersedes`. No recording is mutated; lookups filter via the list.
- **Supersede chain**: the sequence of relations that maps a ChildSlot's `OriginChild` to its current visible representative. Chains can extend over multiple re-fly attempts (AB -> AB' -> AB'' if AB' was a merged crash that the player then re-flew again).
- **Ledger Tombstone**: append-only `LedgerTombstone { TombstoneId, SupersededActionId, RetiringRecordingId, UT }`. Keyed by immutable `ActionId`. v1 scope is narrow — see §1.1 and §6.6.4.
- **ReFlySessionMarker**: singleton persisted struct in `PARSEK` scenario while a re-fly session is active. Written atomically in the same frame as the provisional re-fly recording's creation (§5.6 / §6.3).
- **ERS / ELS**: Effective Recording Set / Effective Ledger Set. See §3.
- **SessionSuppressedSubtree**: narrow forward-only closure from the supersede-target recording, stopping at mixed-parent BranchPoints. Applied during an active re-fly to physical-visibility subsystems only (§3.4).
- **MergeJournal**: single-valued persisted field that carries a staged-commit `Begin` marker across the atomic merge sequence. Cleared only after all irreversible steps complete and durably save.

---

## 3. Effective State Model

Two computed sets; subsystems route all "is this in effect?" questions through them. Supersede affects ERS only; career state is driven by ELS, which v1 filters only by tombstones.

### 3.1 Effective Recording Set (ERS)

```
ERS = { r in RecordingStore.CommittedRecordings
      : r.IsVisible
        AND r.MergeState in {Immutable, CommittedProvisional} }

IsVisible(r)        := NOT exists rel in ParsekScenario.RecordingSupersedes
                             : rel.OldRecordingId == r.Id

IsUnfinishedFlight(r) := r in ERS
                        AND r.MergeState == CommittedProvisional
                        AND r.TerminalKind in {BGCrash, Crashed}          # both flavors
                        AND r.ParentBranchPoint?.RewindPointId != null
                        AND r.IsControllable
```

`IsVisible` is a derived lookup over the append-only supersede relation list. The Recording struct has no `SupersededByRecordingId` field. A committed recording is never mutated post-commit.

`IsUnfinishedFlight` accepts both terminal kinds so a player who merges a crashed re-fly can rewind AGAIN (the merged crash is still itself a new Unfinished Flight). See §6.6 step 2 for the merge rule that produces `CommittedProvisional` on crash outcomes.

### 3.2 Effective Ledger Set (ELS)

```
ELS = { a in Ledger.Actions
      : a.ActionId is present (immutable, stable; generated on load for
                               legacy actions — see §9)
        AND NOT exists t in Ledger.Tombstones
                       : t.SupersededActionId == a.ActionId }
```

**One filter only: tombstones.** v0.4 additionally filtered ELS by "action's source recording in ERS." That was wrong for v1's narrow supersede scope: it retired every action from a superseded recording, contradicting the "contract completions survive supersede" claim. v0.5 drops the recording-level filter. The ONLY way an action exits ELS under this feature is via an explicit tombstone.

Consequence: superseded recordings' ledger actions (contract completions, milestones, facility changes, strategies, tech, non-kerbal-death rep, science, funds) remain in ELS. They continue to contribute to career totals. v1 supersede does not un-commit them.

Actions that are physically removed (tree discard) are gone from `Ledger.Actions` entirely and never appear in ELS.

### 3.3 Session-suppressed subtree (narrow carve-out, physical only)

During an active re-fly, a second narrow carve-out applies to physical-visibility subsystems only — not to ELS.

**Closure rule (forward-only, merge-guarded):**

```
SessionSuppressedSubtree(refly) = closure(refly.OriginChildRecordingId)

def closure(r):
    result = { r }
    for each child c such that r in c.ParentRecordingIds:        # forward direction
        if c.ParentRecordingIds subset-of-or-equal-to result.Ids:
            # c's only parents are inside the subtree; include recursively
            result.add_all(closure(c))
        else:
            # mixed-parent: c has a parent outside the subtree (dock/board merge).
            # DO NOT include c — suppression stops here.
    return result
```

**Consumed by:**
- Ghost playback walker
- Vessel-identity claim / chain-tip tracker
- `GhostMapPresence` (map view + tracking station + CommNet relay visibility)
- `WatchModeController` (camera-follow ghost selection)

**NOT consumed by** (these read ERS/ELS directly; no re-fly carve-out):
- Ledger recalculation engine (funds/science/rep totals)
- Career state UI
- CrewReservationManager (with narrow exception in §3.3.1)
- Contract state derivation (KSP-owned; Parsek does not touch on supersede)
- Milestone flags (KSP-owned; sticky)
- Timeline view
- Resource recalculation
- Unfinished Flights membership
- RewindPoint reap logic
- FacilityUpgrade / Strategy / Tech state derivation

### 3.3.1 Kerbal dual-residence carve-out

A narrow reservation-manager carve-out: kerbals physically embodied in the live re-fly vessel (present in `FlightGlobals.ActiveVessel.GetVesselCrew()` at any point during the session) are exempted from reservation-lock for the session duration. Without this, a kerbal whose ledger `KerbalDeath` event is still in ELS (tombstone not yet written — that happens at merge) would be reservation-locked as dead, silently blocking EVA or crew transfer during the re-fly.

Other reservation effects (kerbals reserved to unrelated still-ghost recordings, stand-in generation for other unreserved slots) remain active.

### 3.4 Subsystem consumer table

| Subsystem | Reads | Writes |
|---|---|---|
| Ghost playback walker | ERS \ SessionSuppressedSubtree | never |
| Vessel-identity claim / chain-tip tracker | ERS \ SessionSuppressedSubtree | never |
| GhostMapPresence | ERS \ SessionSuppressedSubtree | ProtoVessel lifecycle |
| WatchModeController | ERS \ SessionSuppressedSubtree | camera anchoring |
| CrewReservationManager | ERS (live-re-fly crew exempted) | reservations |
| Ledger recalculation | ELS | derived career state |
| Career-state UI (funds / science / rep) | ELS-derived totals | never |
| Contract state (KSP-owned) | KSP's `ContractSystem` | — |
| Milestone flags (KSP-owned) | KSP's `ProgressTracking` | — |
| Timeline view | ERS lifecycle events + ELS actions | never |
| Resource recalculation | ELS (resource-changing actions) | never |
| Unfinished Flights | ERS filtered by IsUnfinishedFlight | never |
| RewindPoint reap | ERS effective of each slot | RP removal only |

**Grep-audit requirement (§11).** No code outside this table walks `RecordingStore.CommittedRecordings` or `Ledger.Actions` directly. Existing raw-access surfaces (GhostMapPresence, tracking station, watch mode, diagnostics, UI) must be converted to ERS/ELS consumers as a prerequisite phase before this feature's main surface area is implemented. CI grep gate enforces.

### 3.5 Invariants

1. **Append-only across the board.** No recording, ledger action, supersede relation, or tombstone is ever mutated or deleted after write. Tree-discard is the only wholesale-removal path; it deletes whole subtrees atomically.
2. **Supersede is a relation.** `RecordingSupersede(oldRec, newRec)` lives in its own persistent list. `IsVisible` is derived. No field on a committed Recording is written post-commit.
3. **Subtree supersede.** When a re-fly supersedes OriginChild, the merge adds `RecordingSupersedeRelation` records for every descendant in the forward-only merge-guarded closure. The whole subtree exits ERS together.
4. **First-time flags are monotonic and KSP-owned.** KSP's `ProgressTracking` holds first-time flags. Parsek does not un-set them on supersede.
5. **Narrow v1 supersede effects on ledger.** Only `KerbalDeath` actions (and rep penalties bundled with them) are tombstoned on supersede. All other action types remain in ELS.
6. **ERS and ELS are computed, not stored.** Derived from their source collections + the supersede list + the tombstone list. Caching is an implementation concern.
7. **Supersede relations once appended are never removed except by whole-tree discard.** Orphan relations (rare; endpoints that never resolve) are logged `Warn` and left in place. Walk logic treats an unresolvable relation as if the chain terminates at the prior node.

---

## 4. Mental Model

### 4.1 Tree evolution across a full flight + re-fly + re-rewind cycle

`(V)` = in ERS (visible, not superseded). `(H)` = hidden (superseded).

Launch:
```
ABC (NotCommitted, live, slot=root)   (V)
```

Staging (split-1): ABC -> AB + C. RP-1 session-provisional.
```
ABC (NotCommitted, terminates at split-1)     (V)
 |
 +-- [slot-1a: AB]  (NotCommitted, BG)          (V)
 +-- [slot-1b: C]   (NotCommitted, live)        (V)
     [RP-1 (session-prov) PidSlotMap={pid_AB:1a, pid_C:1b}
       RootPartPidMap={rootpid_AB:1a, rootpid_C:1b}]
```

AB BG-crashes; player reaches orbit with C; session merges.
```
ABC  (Immutable)                                (V)
 |
 +-- AB  (CommittedProvisional, BGCrash)        (V)  Unfinished Flight
 +-- C   (Immutable, orbit)                     (V)
     [RP-1 PROMOTED persistent]
     Unfinished Flights = {AB}
```

Player invokes RP-1 for AB. Atomically in one frame: load quicksave, strip per PidSlotMap (remove C; leave AB as the active vessel; leave unrelated vessels alone), create provisional re-fly AB' with `CreatingSessionId = sess1` and `SupersedeTargetId = AB.Id`, write `ReFlySessionMarker` naming AB'.
```
ABC  (Immutable)                                (V)
 |
 +-- AB   (CommittedProvisional, BGCrash)       (V)  in SessionSuppressedSubtree
 +-- AB'  (NotCommitted, live, CSId=sess1,
           SupersedeTargetId=AB)                (V)  provisional
 +-- C    (Immutable)                           (V)  ghost
     ReFlySessionMarker sess=sess1
     Unfinished Flights = {AB}   (AB' NotCommitted; not in ERS/predicate)
```

During re-fly: ERS = {ABC, AB, C}. AB' is `NotCommitted` and therefore NOT in ERS per §3.1 (it's the active vessel, a live in-progress recording, not committed data). The ghost walker further filters ERS by `SessionSuppressedSubtree` to exclude AB → plays ABC, C as ghosts. AB' is the live vessel, handled by the normal active-vessel path (not by the ERS filter). ELS includes all AB ledger actions (nothing tombstoned yet). Career UI shows AB's rep penalty still present.

AB' crashes. Player merges.

**Because TerminalKind is Crashed**, the merge rule (§6.6 step 2) commits AB' as `CommittedProvisional`, not `Immutable`. The supersede relation still commits: `RecordingSupersede(AB, AB')` is appended. AB is hidden; AB' is the new slot representative.
```
ABC  (Immutable)                                (V)
 |
 +-- AB    (CommittedProvisional, BGCrash, SUPERSEDED)  (H)
 +-- AB'   (CommittedProvisional, Crashed,     (V)  NEW Unfinished Flight
            supersedes AB via relation)              (effective for slot-1a)
 +-- C     (Immutable)                          (V)
     Tombstones: any KerbalDeath actions in AB scope
     RecordingSupersedes: {AB, AB'}
     Unfinished Flights = {AB'}   (AB' satisfies predicate: visible + CP + Crashed + RP-parent)
```

Player wants to try again. Invokes RP-1 for AB'. Strip + activate + new provisional AB''. Player lands safely this time. Merges. **TerminalKind is Landed** → `Immutable`. Supersede chain extends: `{AB, AB'}, {AB', AB''}`.
```
ABC  (Immutable)                                (V)
 |
 +-- AB    (CP, BGCrash, hidden)                (H)
 +-- AB'   (CP, Crashed, hidden)                (H)
 +-- AB''  (Immutable, Landed, effective slot-1a) (V)
 +-- C     (Immutable)                          (V)
     Tombstones: KerbalDeath from AB + KerbalDeath from AB' (both subtrees)
     RecordingSupersedes: {AB, AB'}, {AB', AB''}
     Unfinished Flights = {}
     RP-1 reap-eligible: slot-1a effective=AB'' (Immutable), slot-1b=C (Immutable) -> reap
```

### 4.2 Key invariants (recap)

1. Tree never shrinks. Supersede is a relation; old recordings stay.
2. Supersede chains can extend through multiple crashed re-flies. Each `CommittedProvisional Crashed` merge creates a new link AND a new Unfinished Flight under the same slot.
3. Only `Immutable` outcomes close the rewind opportunity; `CommittedProvisional` keeps the slot rewindable.
4. Career state reads ELS; v1 supersede only tombstones kerbal-death + bundled rep. Everything else sticks.
5. Ghost-visible systems apply SessionSuppressedSubtree during active re-fly. Career systems do not.

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
    Dictionary<uint,int>  PidSlotMap        Vessel.persistentId  -> SlotIndex (primary)
    Dictionary<uint,int>  RootPartPidMap    rootPart.persistentId -> SlotIndex (fallback;
                                            catches vessels whose vessel-level persistentId
                                            was reassigned between save and load. Root part
                                            persistentId is stable across the save/load cycle
                                            for the same physical part, so it is the right
                                            identity key. NOT Part.flightID, which is
                                            session-scoped and unstable.)
    bool    IsSessionProvisional
    string  CreatingSessionId               if session-provisional, the session GUID
    DateTime CreatedRealTime
    bool    Corrupted                       set true on quicksave validation failure
```

Both maps are populated on the deferred frame after `GamePersistence.SaveGame` returns, using live `FlightGlobals` state. The strip (§6.4) tries PidSlotMap first; if absent for a given vessel, consults RootPartPidMap by the vessel's root part persistentId.

### 5.2 ChildSlot

```
ChildSlot
    int     SlotIndex                      0-based, stable
    string  OriginChildRecordingId         immutable

Derived:
    EffectiveRecordingId()                 walks FORWARD via RecordingSupersedes.
                                           From OriginChildRecordingId, find
                                           rel where rel.OldRecordingId == r;
                                           if found, recurse on rel.NewRecordingId;
                                           else return r.
                                           Cycle detection: maintain visited set.
                                           Tree-scoped: halt if a relation crosses
                                           a tree boundary (v1 does not produce
                                           cross-tree supersedes).
                                           Orphan handling: if rel.NewRecordingId
                                           does not resolve to a recording in
                                           RecordingStore, treat as if the chain
                                           terminates at the prior node; log Warn
                                           once per walk.
```

### 5.3 RecordingSupersedeRelation (new persistent list)

```
RecordingSupersedeRelation
    string  RelationId                     "rsr_" + Guid
    string  OldRecordingId                 the superseded recording (stays in RecordingStore)
    string  NewRecordingId                 the superseding recording
    double  UT                             UT at which the merge occurred
    DateTime CreatedRealTime
```

Stored in `ParsekScenario.RecordingSupersedes`. Append-only. Never mutated. Removed only by whole-tree discard. Orphan relations (rare; only from tree-discard-during-merge) stay in place with a Warn log.

### 5.4 BranchPoint additions

```
string RewindPointId                       null if <2 controllable children; else non-null
```

### 5.5 Recording additions

v0.5 confirms v0.4's decision to REMOVE `SupersededByRecordingId` from the Recording schema. Visibility is a derived lookup.

```
enum MergeState                            NotCommitted / CommittedProvisional / Immutable

string CreatingSessionId                   null outside sessions; set to session GUID for
                                           recordings created during a re-fly (including
                                           nested BG-recorded children). Used for load-time
                                           spare-set logic (§6.9).

string SupersedeTargetId                   TRANSIENT. Set only on NotCommitted provisional
                                           re-fly recordings; indicates intent. On merge,
                                           this is replaced by a concrete
                                           RecordingSupersedeRelation in §6.6 step 3; the
                                           field is cleared. On load, any Immutable or
                                           CommittedProvisional recording with a non-empty
                                           SupersedeTargetId is logged Warn; the field is
                                           treated as cleared (legacy-write safety).
```

Computed helpers (all derived):
```
IsVisible(r)            := NOT exists rel : rel.OldRecordingId == r.Id
IsSuperseded(r)         := NOT IsVisible(r)
SupersedingChain(r)     := walk forward via RecordingSupersedes
IsControllable(r)       := has ModuleCommand parts OR is an EVA kerbal
```

### 5.6 Ledger additions

```
Every LedgerAction requires a stable immutable ActionId (schema precondition;
legacy migration in §9).

LedgerTombstone
    string  TombstoneId                    "tomb_" + Guid
    string  SupersededActionId             immutable ActionId
    string  RetiringRecordingId            the NEW recording whose merge caused this
    double  UT                             UT of the merge
    DateTime CreatedRealTime
```

Append-only. Multiple tombstones for the same ActionId are tolerated (extended supersede chains). Filter is "at least one tombstone exists."

### 5.7 ReFlySessionMarker

```
ReFlySessionMarker (singleton; present in PARSEK scenario iff a re-fly is active)
    string  SessionId                      unique GUID per invocation/retry
    string  TreeId                         RecordingTree this re-fly belongs to
    string  ActiveReFlyRecordingId         the NotCommitted provisional re-fly recording
    string  OriginChildRecordingId         supersede target
    string  RewindPointId                  invoked RP
    double  InvokedUT                      UT at invocation
    DateTime InvokedRealTime               wall-clock
```

**Lifecycle — atomic in-frame write.** §6.3 phases:
1. (Same frame, same synchronous code path:) Create the provisional re-fly recording `AB'` with fresh `Id`, `MergeState = NotCommitted`, `CreatingSessionId = sessionId`, `SupersedeTargetId = selectedChild.Id`. Add to RecordingStore.
2. (Same frame, same synchronous code path, immediately after step 1:) Write the `ReFlySessionMarker` to `ParsekScenario`.

KSP's `OnSave` cannot preempt the synchronous code path between step 1 and step 2. There is no window in which a save would capture step 1 without step 2. v0.4's rescue path for that window is DROPPED — the problem does not exist under atomic execution.

**Cleared** on: re-fly merge (recording transitions out of NotCommitted), re-fly discard (return-to-Space-Center or quit-without-merge after return), retry (new SessionId written; other fields unchanged), full-revert, load-time validation failure.

**Validated on load:** verify all six fields consistent with scenario state. On any failure: clear marker + `Warn`. Zombie cleanup then proceeds normally.

### 5.8 MergeJournal

```
MergeJournal (singleton; present only during a staged-commit merge)
    string  Phase                          "Begin" while a merge is in staged commit
    string  SessionId                      merge session GUID
    double  StartedUT
    DateTime StartedRealTime
```

Written at §6.6 step 1 (in memory) and durably saved at step 8. Cleared at §6.6 step 13; durably saved at step 14. Its presence on load triggers a finisher (§6.9 step 2) to complete any crashed-mid-merge state. Idempotent finisher.

### 5.9 ParsekScenario persistence additions

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
            ROOT_PART_PID_MAP
                ENTRY pid = 11223344 slotIndex = 0
                ENTRY pid = 99887766 slotIndex = 1
            CHILD_SLOT
                index = 0
                originChildRecordingId = rec_01234
            CHILD_SLOT
                index = 1
                originChildRecordingId = rec_01235
        ...
    RECORDING_SUPERSEDES
        REL
            id = rsr_abcd
            oldRec = rec_01234
            newRec = rec_03333
            ut = 1742800.00
            createdRealTime = 2026-04-17T22:10:00Z
        ...
    LEDGER_TOMBSTONES
        TOMBSTONE
            id = tomb_bcd1
            supersededActionId = act_09876
            retiringRecordingId = rec_03333
            ut = 1742800.00
        ...
    REFLY_SESSION_MARKER (optional; present only during active session)
        sessionId = rf_c3d4
        ...
    MERGE_JOURNAL (optional; present only during staged-commit merge)
        phase = Begin
        sessionId = rf_c3d4
        startedUT = 1742800.00
        ...
```

Recording schema additions:
```
RECORDING
    ...
    mergeState = CommittedProvisional
    creatingSessionId =                        # empty outside sessions
    supersedeTargetId =                        # empty in persisted form (transient only)
```

### 5.10 Directory layout

```
saves/<save>/
    persistent.sfs                             (KSP standard)
    Parsek/
        Recordings/
            <recordingId>.prec                 (existing)
            <recordingId>_vessel.craft         (existing)
            <recordingId>_ghost.craft          (existing)
        RewindPoints/
            <rewindPointId>.sfs                (NEW; KSP-format quicksave)
```

v0.3's `<recordingId>_split.craft` (SplitTimeSnapshot) is DROPPED; v0.4 dropped it; v0.5 confirms. No defined v1 consumer.

### 5.11 Unfinished Flights UI group

Virtual node in `GroupHierarchyStore`. Membership derived per frame from ERS filtered by `IsUnfinishedFlight`. Not manually editable. Drag-into rejected. Cannot be hidden.

---

## 6. Behavior

### 6.1 At a multi-controllable split

Triggered from `ParsekFlight.DeferredJointBreakCheck`, `DeferredUndockBranch`, or the EVA handler after the classifier identifies >=2 controllable entities.

**RPs are speculative.** At split time, we do not yet know whether any sibling will need a rewind. The purpose of Rewind Points is to let the player re-fly a sibling that ended badly — a destroyed booster, a dead kerbal, a crashed EVA — *not* to offer rewind on every split regardless of outcome. A kerbal who EVAs and then coasts to a stable orbit, or a booster that happens to land safely on its own, has nothing to re-do: the recording spawns them at the correct final state, the player never needs to take over. The RP for such a split is useless.

Since we can only classify outcomes at session merge (after each vessel's terminal state is known), v1 writes an RP **speculatively** on every multi-controllable split and then **reaps it in the same session merge transaction** if no sibling ended up as an Unfinished Flight (see §6.6 step 9 for the reap check, §6.6 step 10 for the file delete). Net effect:

- **All-stable split** (e.g. EVA to orbit, booster lands via chute, undock with both halves completing their missions): RP is written at the split, survives only until session merge, then reaped. Transient quicksave lives one session, then the file is gone. The player never sees a Rewind Point UI entry for this split.
- **Split with >=1 unfinished sibling** (e.g. booster BG-crashes into ground, EVA kerbal left stranded): RP is written at the split, survives session merge, persists until all siblings resolve (merged as immutable or superseded). The player sees Unfinished Flights entries for the unfinished siblings and can invoke the RP.

This is the only sensible behavior given we cannot predict outcomes at split time; the alternative (defer quicksave until merge) would require capturing split-moment world state some other way, which is exactly what the KSP quicksave provides. The cost of being wrong-optimistic (writing a speculative quicksave that reaps at merge) is small: one transient ~1-2 MB file for the duration of one session, deleted automatically at merge.

1. Child Recordings created by the existing flight-recorder pipeline. Their `Id` values are stable.
2. Construct RP with ChildSlots, empty PidSlotMap and RootPartPidMap.
3. `IsSessionProvisional` and `CreatingSessionId` reflect any currently-active `ReFlySessionMarker`.
4. **Defer the quicksave write one frame** (coroutine -> next FixedUpdate).
5. On the deferred frame:
   - **Scene guard:** if `HighLogic.LoadedScene != GameScenes.FLIGHT`, abort RP creation, log `Warn`.
   - Force `TimeWarp.SetRate(0, instant:true)` if rate > 1. Restore after save.
   - Call `GamePersistence.SaveGame(<tempName>, "persistent", SaveMode.OVERWRITE)`. **KSP writes to the root save dir** per existing convention.
   - Atomically move to `saves/<save>/Parsek/RewindPoints/<rewindPointId>.sfs` via `FileIOUtils.SafeMove` (tmp+rename).
   - `SaveUT = Planetarium.GetUniversalTime()` at the save call.
   - Populate `PidSlotMap` and `RootPartPidMap` by iterating `FlightGlobals.Vessels` and matching the ChildSlot's `OriginChildRecordingId` to each vessel's current recording id. If a slot's lookup fails, log `Warn`; the slot's Rewind is disabled but other slots remain usable.
   - Write `BranchPoint.RewindPointId`.
   - Append RP to `ParsekScenario.RewindPoints`.
6. Log `Info`.

Failure modes (disk full, permissions, save exception): RP not created; BranchPoint.RewindPointId stays null; split proceeds as today; log `Warn`.

### 6.2 Between split and merge

Focused vessel records normally. Unfocused controllables BG-recorded. All `NotCommitted`.

On session merge:
- Focused vessel's recording -> `Immutable`.
- BG-recorded controllable children whose parent BranchPoint has `RewindPointId != null` -> `CommittedProvisional`.
- Session-provisional RPs promote persistent.
- Unfinished Flights recomputes.

### 6.3 Invoking a Rewind Point

UI preconditions:
- RP not Corrupted.
- Quicksave file exists; PartLoader deep-parse precheck passes.
- No other re-fly session is currently active (§7.5).

Confirmation dialog (conceptual):
> Rewind to stage separation of <ParentVesselName> at UT <formatted MET>?
> <ChildVesselName> spawns live at the split moment. Merged siblings play as ghosts.
> Career state during this attempt stays as it is now (rep penalties from the previous crash remain). Supersede retires only kerbal-death events on merge; contracts, milestones, and other career state are unchanged by the supersede.
> Cancel or Proceed.

On Proceed:
1. `SessionId = Guid.NewGuid()`.
2. Capture reconciliation bundle (§6.4).
3. Load the RP's quicksave (existing rewind-to-quicksave path; substituted filename).
4. Post-load, on the first scene-ready frame, **atomically in one synchronous code path**:
   - Apply reconciliation.
   - Run post-load strip (§6.4 step 4).
   - Activate selected child's vessel (§6.4 step 5).
   - **Phase 1:** create provisional re-fly Recording `AB'` (fresh Id, `MergeState = NotCommitted`, `CreatingSessionId = SessionId`, `SupersedeTargetId = selectedChild.Id`). Add to RecordingStore.
   - **Phase 2:** write `ReFlySessionMarker`. Same frame, same call chain, no yield.
   - Run `LedgerOrchestrator.Recalculate()`.
5. Log `Info`.

Atomicity: phases 1 and 2 are adjacent method calls inside the same `Invoke` coroutine step. KSP cannot serialize `ParsekScenario` between them. No save can capture step 1 without step 2.

### 6.4 Reconciliation + post-load strip + activation

Reconciliation bundle (captured pre-load, reapplied post-OnLoad):

| Domain | Post-load source | Preserved from in-memory |
|---|---|---|
| Planetarium.UT | Quicksave = SaveUT | — |
| KSP raw funds / science / rep | Quicksave | caught up by ELS recalc |
| KSP vessels | Quicksave | — (strip + activate) |
| `RecordingStore` | Discarded | Preserved (full history) |
| `Ledger.Actions` | Discarded | Preserved |
| `Ledger.Tombstones` | Discarded | Preserved |
| `ParsekScenario.RewindPoints` | Discarded | Preserved |
| `ParsekScenario.RecordingSupersedes` | Discarded | Preserved |
| `CrewReservationManager` | Discarded | Preserved (recomputed post-apply) |
| `RecordingTree.PreTreeFunds/Science` | Quicksave (correctly set at tree creation) | — |
| `GroupHierarchyStore` | Discarded | Preserved |
| `MilestoneStore` (legacy) | Discarded | Preserved |
| `ReFlySessionMarker` | — | Written in §6.3 step 4 phase 2 |

**Step 4: post-load strip (authoritative matching).**

For each vessel `v` in `FlightGlobals.Vessels`:
1. **Ghost ProtoVessel guard.** If `v.persistentId in GhostMapPresence.ghostMapVesselPids`: skip. Do not strip; it is a Parsek-spawned map presence.
2. **Primary match via PidSlotMap.** If `rp.PidSlotMap.TryGetValue(v.persistentId, out slotIdx)`: handle based on `slotIdx == selectedSlotIdx` (step 5) vs. not (strip via existing ghost-conversion path and register Recording for ghost playback).
3. **Fallback match via RootPartPidMap.** If primary miss but `v.rootPart != null` and `rp.RootPartPidMap.TryGetValue(v.rootPart.persistentId, out slotIdx)`: handle as in step 2. Log `Verbose` "[Rewind] Strip fallback match via root-part persistentId for vessel=<name>". (Note: `Part.persistentId`, not `Part.flightID` — `flightID` is session-scoped and changes, while `persistentId` is the stable cross-save/load identifier for the same physical part.)
4. **Else: leave alone.** The vessel does not belong to this RP's slot set (pre-existing stock vessel, different tree, debris, etc.).

Log `Info` "[Rewind] Strip: slotsStripped=[<indices>] slotSelected=<idx> ghostsGuarded=<N> leftAlone=<M>".

**Step 5: activate selected child's vessel.** `FlightGlobals.SetActiveVessel(selectedVessel)`. PID unchanged from quicksave. Selected vessel's situation/resources/crew are authoritative at SaveUT; the crew list becomes the canonical live crew for §3.3.1.

**Step 6: create provisional re-fly Recording.** Executed atomically with marker write (§6.3 step 4 phases).

### 6.5 Within a re-fly session

Ghost walker and physical-visibility subsystems enumerate `ERS \ SessionSuppressedSubtree`. Career subsystems enumerate ERS/ELS directly (no carve-out).

SessionSuppressedSubtree closure is forward-only with mixed-parent halt (§3.3). Multi-parent descendants where at least one parent is outside the subtree are NOT suppressed.

Nested multi-controllable splits during the session create session-provisional RPs with `CreatingSessionId = activeSession.SessionId`. Child recordings born in those splits also carry `CreatingSessionId`. On merge, all promote to persistent; on discard, all purge.

### 6.6 Merging a re-fly session (staged commit, journaled)

Merge is journaled to survive crashes between steps. The journal entry lives in scenario; the finisher in §6.9 step 2 completes interrupted merges on the next load.

Steps in order:

1. **In-memory:** `MergeJournal.Phase = "Begin"`, `SessionId`, `StartedUT`.

2. **Recordings commit.** For each `NotCommitted` recording in the session:
   - If it is the provisional re-fly (carries `SupersedeTargetId`):
     - If `TerminalKind in {Landed, Orbited, Recovered, OtherStable}`: `MergeState = Immutable`. Clear `SupersedeTargetId`.
     - If `TerminalKind in {BGCrash, Crashed}`: `MergeState = CommittedProvisional`. Clear `SupersedeTargetId`. (The recording still satisfies `IsUnfinishedFlight`; the slot remains rewindable.)
   - If it is a BG-recorded nested child under a split whose parent BranchPoint has `RewindPointId != null`: `MergeState = CommittedProvisional`.
   - Otherwise: `MergeState = Immutable`.

3. **Supersede relation commit.** Append `RecordingSupersedeRelation { OldRecordingId = selectedChild.Id, NewRecordingId = reflyRec.Id, UT = now }`. For each descendant `d` in `ForwardMergeGuardedSubtree(selectedChild)`: append `RecordingSupersedeRelation { OldRecordingId = d.Id, NewRecordingId = reflyRec.Id, UT = now }`. Subtree supersede.

4. **v1 tombstones — narrow scope.** Scan ledger for actions `a` where:
   - (a.RecordingId in the supersede-target subtree) OR
   - (a.RecordingId == null AND payload attribution links `a` to the subtree)

   For each matching `a`, check `a.Type` against the v1 tombstone-eligible list:
   - `KerbalDeath` — yes
   - `ReputationPenalty` / `ReputationEarning` *scoped via payload bundle to a tombstoned KerbalDeath* — yes
   - **All other types — NO.** Contract actions, Milestones, Facility, Strategy, Tech, general Funds/Science/Rep deltas stay in ELS.

   Emit LedgerTombstones for eligible actions. Log `Info` "[LedgerSwap] Tombstoned <N> actions (KerbalDeath=<d>, repBundled=<r>); all other types remain in ELS per v1 narrow scope".

5. **Career UI advisory log** (reinforcement): `Info` "[Supersede] Narrow v1 effects: physical playback replaced; <N> kerbal-death events retired. Contract/milestone/facility/strategy/tech/science/funds state unchanged."

6. **CrewReservationManager recomputes** from ERS (superseded recordings excluded via IsVisible). Kerbals whose death was tombstoned return to active via the normal walk.

7. **Promote session-provisional RPs to persistent.** For each RP with `IsSessionProvisional == true AND CreatingSessionId == activeSession.SessionId`: set `IsSessionProvisional = false`, clear `CreatingSessionId`.

8. **Durable Save #1 (first safe point).** ScenarioModule OnSave. All in-memory changes from steps 1-7 are now on disk. Journal on disk = "Begin". Marker still present on disk. RP quicksaves still on disk.

9. **RP reap check.** For each persistent RP: walk ChildSlots. EffectiveRecordingId for each slot. If no slot's effective is an Unfinished Flight: RP is reap-eligible. Note: because `IsUnfinishedFlight` accepts `BGCrash` AND `Crashed`, an RP whose re-fly was merged as a `CommittedProvisional Crashed` will NOT reap; the slot remains rewindable.

10. **Irreversible file ops.** For each reap-eligible RP: delete its quicksave file (`saves/<save>/Parsek/RewindPoints/<id>.sfs`). Remove from `ParsekScenario.RewindPoints`. Idempotent: deleting an already-deleted file is a no-op.

11. **In-memory:** clear `ReFlySessionMarker`.

12. **Durable Save #2.** Reaped RPs + cleared marker on disk. Journal on disk = still "Begin".

13. **In-memory:** clear `MergeJournal` (Phase = empty / absent).

14. **Durable Save #3.** Journal cleared on disk.

15. Run `LedgerOrchestrator.Recalculate()` (idempotent).

16. Log `Info` summary.

**Failure recovery (on-disk state x crash-point matrix):**

| Crash between | On-disk state | Recovery on next load |
|---|---|---|
| 1 and 8 | No journal on disk. No mutations durable. | Normal load. The in-memory attempt is lost; user retries merge. |
| 8 and 10 | Journal=Begin, marker present, RPs all present, recordings+supersedes+tombstones durable. | Finisher (§6.9 step 2) runs: reap check, delete eligible quicksaves, clear marker, Durable Save, clear journal, Durable Save. |
| 10 and 12 | Journal=Begin, marker present, some RP quicksaves deleted + some still in scenario list. | Finisher: reap check skips files already missing (idempotent), removes scenario entries, clears marker, Durable Save, clears journal, Durable Save. |
| 12 and 14 | Journal=Begin, marker absent, reap complete. | Finisher: nothing structural to do; clear journal, Durable Save. |
| After 14 | Journal absent, marker absent. | Normal load. |

**Finisher is triggered by journal presence, not by marker state.** This is the v0.5 correction of v0.4's condition "journal=Begin AND marker cleared" which missed the 8-and-12 windows.

### 6.7 Revert-to-Launch during re-fly

Stock Revert intercepted. 3-option dialog:
- **Retry**: discard current provisional, generate new `SessionId`, update marker (NOT clear), reload same quicksave, create new provisional re-fly.
- **Full Revert**: clear marker, invoke tree-discard. All under-tree RPs and recordings removed.
- **Cancel**: resume.

### 6.8 Session end without merge

- Return to Space Center: clear marker + discard provisional + purge session-provisional RPs.
- Quit without Space Center: marker stays. Load-time validation decides (§6.9).

### 6.9 Load-time sweep (single pass, gather-then-delete)

1. KSP loads save; ScenarioModule OnLoad populates all state.

2. **Journal finisher.** If `MergeJournal.Phase == "Begin"`:
   - Determine marker state on-disk (present or cleared).
   - Run reap check idempotently on all persistent RPs (deletes files not already missing, removes scenario entries).
   - Clear marker if still present.
   - Durable save (clears both marker and half-reaped state on disk).
   - Clear journal. Durable save.
   - Log `Info` "[MergeJournal] Finisher completed for sess=<sid>".

3. **Marker validation** (if marker present after step 2):
   - `ActiveReFlyRecordingId` exists; `MergeState == NotCommitted`.
   - `OriginChildRecordingId` in ERS; `CommittedProvisional`; `TerminalKind in {BGCrash, Crashed}`; parent BP has RP.
   - `RewindPointId` exists; state consistent (persistent OR session-provisional with matching `CreatingSessionId`).
   - `TreeId` matches origin's tree.
   - All pass: `markerValid = true`.
   - Any fail: `markerValid = false`, clear marker, log `Warn`.

4. **Gather spare set.** If `markerValid`:
   - All recordings with `CreatingSessionId == marker.SessionId` are spared.
   - All RPs with `CreatingSessionId == marker.SessionId` are spared.

5. **Gather discard set.**
   - Recordings with `MergeState == NotCommitted` AND parent BP has RewindPointId AND not in spare set.
   - RPs with `IsSessionProvisional == true` AND not in spare set.

6. **Delete as one pass.** Sidecar files for each to-discard recording; quicksave file for each to-discard RP; scenario lists updated.

7. **Supersede consistency log.** Iterate `RecordingSupersedes`; for each relation, verify both endpoints exist. Orphans: logged `Warn` (NOT removed — §3.5 invariant 7). Walk logic separately handles unresolvable nodes.

8. **Orphan tombstone check.** For each `LedgerTombstone`: verify `SupersededActionId` exists. Orphans logged `Warn`; kept.

9. **Legacy-field stray check.** For each Immutable or CommittedProvisional recording: if `SupersedeTargetId` is non-empty, log `Warn` and treat as cleared (load-path legacy safety).

10. Run `LedgerOrchestrator.Recalculate()`.

11. Log `Info` summary: "<N> zombies discarded, <M> session-prov RPs purged, <J> journal finisher ran? <Y/N>, <S> supersede orphans, <T> tombstone orphans, <L> RPs loaded, <R> supersede relations loaded".

### 6.10 Tree discard

Revert-to-Launch (or Full Revert dialog option): whole-tree discard.
- Remove all recordings under the tree (sidecar files deleted).
- Reap all RPs under the tree (quicksave files deleted).
- Remove all supersede relations where either endpoint is in the tree.
- Remove all ledger actions scoped to tree recordings + matching tombstones.
- Clear reservations for tree kerbals; CrewReservationManager recomputes.
- Clear marker if it references the tree.

Whole-tree supersede relation removal is the ONLY path that deletes supersede relations (§3.5 invariant 7).

---

## 7. Edge Cases

Each: scenario / expected / v1 verdict.

### 7.1 Single-controllable split
No RP. Debris path unchanged.

### 7.2 3+ controllable children at one split
One RP with N ChildSlots + N entries in each PID map. Player iterates. Handled.

### 7.3 Scene-transition during deferred save
§6.1 step 5 scene guard aborts RP creation; log Warn. Handled.

### 7.4 Partial PidSlotMap failure at split
One slot's lookup fails at split-time; RP still created with partial maps. That slot's row has disabled Rewind button + tooltip. Other slots usable. Log Warn. Handled.

### 7.5 Invoking RP while a re-fly is active
UI guard + dialog. No nested sessions in v1. Handled.

### 7.6 Re-fly target with BG-recorded descendants
SessionSuppressedSubtree includes descendants (forward-only closure). Merge step 3 adds supersede relations for each descendant. Handled.

### 7.7 Cross-tree dock during re-fly
Station S from a different tree's Immutable recording is real at its chain tip. Re-fly can physically dock. Dock event added to re-fly's recording. No cross-tree merge; S's tree not modified. Handled.

### 7.8 Non-Parsek stock vessel interaction
Standard. Handled.

### 7.9 Visual divergence for cross-recording stock-vessel
Ghost-B recorded dock with S at UT=X; live AB' also docks with S at UT=X-100. S is moved by AB'. Ghost-B's playback tries to dock with S-where-ghost-B-remembers-it. Accepted v1 visual limitation.

### 7.10 EVA duplicate on load
Strip detects kerbal in EVA form AND in a source vessel crew. Canonical live location = selected child's crew manifest; dedup via KSP crew removal. Handled.

### 7.11 Nested session-provisional cleanup on parent discard
CreatingSessionId on nested RP = discarded-session's id. Load-sweep spare-set logic removes it. Handled.

### 7.12 F5 mid-re-fly + quit + load
Marker validates; all session-tagged recordings AND RPs spared; re-fly resumes. Atomic phase 1+2 means no save can capture an intermediate state. Handled.

### 7.13 Contract supersede is a no-op on career state
BG-crash completed contract X. Player re-flies, merges. ContractComplete action is NOT tombstoned (not v1-eligible). Contract remains complete in KSP's `ContractSystem`. Rep bonus stays. Player keeps their win. Handled with documented v1 behavior.

### 7.14 Contract failed by BG-crash; re-fly succeeds
BG-crash failed contract X. v1 does NOT un-fail. Contract stays failed. Documented limitation; re-fly is a visual replay, not a contract rescue.

### 7.15 Milestone earned by superseded recording
First-time flag is KSP-owned and sticky. v1 never un-sets. Handled.

### 7.16 Kerbal death + recovery
KerbalDeath + bundled rep tombstoned at merge. Reservation walker re-derives. Kerbal returns to active. (v1 tombstone-eligible.) Handled.

### 7.17 Re-fly crashes; player merges
Re-fly commits as `CommittedProvisional` with `TerminalKind = Crashed`. Supersede relation appends (AB -> AB'). AB' satisfies `IsUnfinishedFlight` (visible + CP + Crashed + RP-parent). Unfinished Flights now lists AB'. Player CAN invoke RP-1 again to try once more. Dialog copy:
> Your attempt crashed. Merging it makes the crash the current effective record for this split, in place of the previous crash. You can try again — the crashed attempt still shows up in Unfinished Flights. Any kerbal deaths in this attempt supersede any deaths in the previous attempt.
Handled.

### 7.18 Re-fly reaches stable outcome; player discards at dialog
Provisional discarded; marker cleared; original BG-crash remains Unfinished; RP durable. Handled.

### 7.19 Simultaneous multi-controllable splits
Two independent RPs; two quicksave writes serial through KSP. Handled.

### 7.20 F9 during re-fly
Standard quickload + auto-resume. Handled.

### 7.21 Warp during re-fly
Standard Parsek warp. Handled.

### 7.22 Rewind click during scene transition
UI guard. Handled.

### 7.23 Two children crash at same location
Independent rewinds. Handled.

### 7.24 Re-fly vessel with no engines
Allowed. Handled.

### 7.25 Drag Unfinished Flight into manual group
Rejected. Handled.

### 7.26 Mod-modified joint-break / destruction
Classifier is ModuleCommand-based. Handled.

### 7.27 High physics warp at split
Forced to 0 during save. Handled.

### 7.28 Disk usage diagnostics
Settings > Diagnostics shows total RP disk usage. Handled.

### 7.29 Mod-part missing on re-fly load
Deep-parse precondition in §6.3. RP marked Corrupted. Handled.

### 7.30 Cannot hide Unfinished Flights group
No hide option. Handled.

### 7.31 Non-crashed BG sibling
Terminal = Landed (BG-determined). `IsUnfinishedFlight` false. Sits CommittedProvisional. Accepted limitation.

### 7.32 Classifier drift across versions
Old RPs retained; no reclassify. Log Verbose. Handled.

### 7.33 Rename / hide on Unfinished Flight row
Rename persists to Recording. Hide warns. Handled.

### 7.34 Corrupted quicksave on invocation
RP marked Corrupted. Rewind disabled. Handled.

### 7.35 Auto-save concurrent with RP save
One-frame defer + root-then-move + KSP serial save pipeline. Handled.

### 7.36 Rewind while unrelated new mission in-flight
Guard dialog: current M2 provisional discarded, then proceed. Handled.

### 7.37 Pre-existing vessel reservation conflict during re-fly
§3.3.1 carve-out exempts live re-fly crew from reservation-lock for the session. Handled.

### 7.38 Strip encounters ghost ProtoVessel
§6.4 step 1 ghost guard. Handled.

### 7.39 Strip encounters unrelated committed real vessel
§6.4 step 4 — not in this RP's PidSlotMap or RootPartPidMap → left alone. Handled.

### 7.40 SessionSuppressedSubtree with mixed-parent descendant
§3.3 closure halts at mixed-parent node. Descendant c remains in ERS. Handled.

### 7.41 Null-scoped action in supersede scope
v1 tombstones only KerbalDeath (and bundled rep). Other null-scoped actions (deadline ContractFail, stand-in generation, etc.) are NOT tombstoned. They stay in ELS. Handled with documented narrow scope.

### 7.42 KSC action during active re-fly
Player doesn't leave flight during re-fly (typically). If a KSC action fires via mod or edge: added to ledger with null RecordingId; not in supersede scope; stays ELS. Handled.

### 7.43 Chain extends through multiple merged crashes
Each `CommittedProvisional Crashed` merge extends the supersede chain (AB -> AB' -> AB'') and the Unfinished Flight moves along with the chain's tip. EffectiveRecordingId walks forward through all relations. Handled (§4.1).

### 7.44 Vessel-destruction rep penalty
Original BG-crash had a vessel-destruction rep penalty (not kerbal-death; just "vessel blew up"). v1 does NOT tombstone general `ReputationPenalty` actions. Only the kerbal-death-bundled rep retires. Vessel-destruction rep stays. Documented v1 limitation.

### 7.45 Merge interruption recovery
Journaled commit + finisher triggered by journal-Begin on load, regardless of marker state. §6.6 matrix covers all 5 crash windows. Handled.

### 7.46 EffectiveRecordingId walk direction + orphan handling
Walks forward via RecordingSupersedes. Cycle detection. Orphan relation (new endpoint missing) treated as chain terminator at prior node; log Warn. Orphan is NOT removed (invariant 7). Handled.

### 7.47 Orphan supersede relation on load
Step 7 of load sweep: log Warn, do not delete. Walk continues treating the orphan as if the chain terminates there. Handled.

### 7.48 AB' merged Immutable but player wants to retry anyway
They cannot. Immutable seals the slot. The player-facing consequence matches §7.17's dialog copy: committing a stable outcome closes the rewind opportunity. To retry, the player must Full-Revert (discards the whole tree). Handled with documented behavior.

---

## 8. What Doesn't Change

- **Flight-recorder principle 10** (additive-only). Enforced rigorously by: supersede is a relation, not a field; tombstones are append-only; no field on committed Recording is ever written post-commit; orphan supersede relations are never deleted outside tree-discard.
- **Single-controllable split path.** Unchanged.
- **Merge dialog UI.** Unchanged shell; v1 adds a warning line about narrow supersede effects.
- **Ghost playback engine.** Reads ERS via walker; SessionSuppressedSubtree is upstream.
- **Active-vessel ghost suppression.** Natural ERS filter.
- **Ledger recalculation engine.** Reads ELS; no engine change.
- **Rewind-to-launch.** Unchanged path.
- **Loop / overlap / chain.** Read ERS.
- **Recording sidecar format.** No changes (SplitTimeSnapshot dropped).
- **Reservation manager internals.** Re-derivation from ERS is existing; the carve-out in §3.3.1 is a single-method filter.
- **KSP sticky-state subsystems** (`ContractSystem`, `ProgressTracking`, `ScenarioUpgradeableFacilities`, `StrategySystem`, `ResearchAndDevelopment`, etc.). v1 never touches any of them on supersede.

---

## 9. Backward Compatibility

- **Old saves without RPs.** Load cleanly. Unfinished Flights empty.
- **Binary -> tri-state MergeState.** "committed" -> Immutable; "in-progress" -> NotCommitted.
- **TerminalKind extension (BGCrash).** Legacy `Destroyed` with BG-recording lineage migrates to `BGCrash` on first post-upgrade load. Idempotent. Pre-feature BG-crashed siblings do NOT appear as Unfinished Flights (parent BP has no RP).
- **Ledger ActionId migration.** Deterministic hash-based IDs for legacy actions. Idempotent. One-time migration log line.
- **Recording schema additions.** `CreatingSessionId`, `SupersedeTargetId` optional. v0.3's `SupersededByRecordingId` (never shipped) is rejected on load with Warn.
- **New persistent lists.** `RecordingSupersedes`, `LedgerTombstones`, optional `REFLY_SESSION_MARKER`, optional `MERGE_JOURNAL`. All optional; empty on old saves.
- **Format version.** No bump; additive.
- **Cross-save portability.** `saves/<save>/Parsek/RewindPoints/` travels with save dir.
- **Feature removal.** All new entities are ignorable as orphans.

---

## 10. Diagnostic Logging

Tags: `Rewind`, `RewindSave`, `Supersede`, `LedgerSwap`, `UnfinishedFlights`, `RewindUI`, `ERS`, `ELS`, `ReFlySession`, `MergeJournal`.

### 10.1 RP lifecycle
- Create: `Info` "[Rewind] RP id=<id> bp=<bpId> saveUT=<x> splitUT=<y> slots=<N> sessionProv=<b> creatingSess=<sid>"
- PidSlotMap + RootPartPidMap built: `Verbose` "[Rewind] RP=<id> primaryMap=<N> fallbackMap=<M>"
- Scene-guard abort: `Warn` "[RewindSave] Aborted scene=<loaded>"
- Save written: `Verbose` "[RewindSave] Wrote rp=<id> path=<path> size=<bytes>B ms=<t>"
- Save failed: `Warn` "[RewindSave] Failed rp=<id>: <reason>"
- Promote session-prov -> persistent: `Verbose` "[Rewind] Promoted rp=<id>"
- Reap: `Verbose` "[Rewind] Reaped rp=<id>"
- Tree discard purge: `Info` "[Rewind] Purged <N> RPs on tree=<id> discard"

### 10.2 Invocation (atomic phase 1+2)
- Click: `Info` "[RewindUI] Invoked rec=<id> rp=<rpId>"
- Marker+recording atomic write: `Info` "[ReFlySession] Started sess=<sid> tree=<tId> active=<rId> origin=<ocId> rp=<rpId>"
- Strip: `Info` "[Rewind] Strip stripped=[<indices>] selected=<idx> ghostsGuarded=<N> leftAlone=<M> fallbackMatches=<f>"
- Post-activate sanity: `Warn` (on drift) "[Rewind] Drift vessel=<n> delta=<m>m"

### 10.3 Session suppression
- Session start: `Info` "[ReFlySession] Start. SuppressedSubtree=[<ids>]"
- Session end: `Info` "[ReFlySession] End reason=<merge|discard|retry|fullRevert|loadInvalid>"

### 10.4 Supersede / LedgerSwap (v1 narrow scope)
- Supersede relation: `Info` "[Supersede] rel=<id> old=<oldId> new=<newId>"
- Subtree supersede count: `Info` "[Supersede] Added <N> descendant supersede relations"
- Tombstones: `Info` "[LedgerSwap] Tombstoned <N> actions (KerbalDeath=<d> repBundled=<r>); <M> actions matched subtree but type-ineligible (breakdown: contract=<c> milestone=<m> facility=<f> strategy=<s> tech=<t> science=<sc> funds=<fn> rep-unbundled=<ru>)"
- Advisory log: `Info` "[Supersede] Narrow v1: physical playback replaced; career state unchanged except kerbal deaths"
- Reservation recompute: `Info` "[LedgerSwap] CrewReservation re-derived; kerbals returned active: [<names>]"
- Recalc totals: `Verbose` "[LedgerSwap] Recalc: funds d=<x> sci d=<y> rep d=<z>"

### 10.5 Unfinished Flights
- Rate-limited recompute: `Verbose` "[UnfinishedFlights] <N> entries"
- +/- per row: `Verbose`
- Drag reject: `Verbose`

### 10.6 Revert dialog
- Shown, choice, cancel: see v0.4 §10.6.

### 10.7 Load sweep
- Journal finisher: `Info` "[MergeJournal] Finisher triggered (marker=<present|absent>); reaped=<R>, cleared marker=<b>"
- Marker valid: `Info` "[ReFlySession] Marker valid; spare set=<N>"
- Marker invalid: `Warn` "[ReFlySession] Marker invalid field=<f>; cleared"
- Zombies: `Info` "[Rewind] Zombies discarded=<N>"
- Session-prov RPs purged: `Info` "[Rewind] Purged session-prov RPs=<M>"
- Supersede orphan: `Warn` "[Supersede] Orphan relation=<id> oldResolved=<b> newResolved=<b>"
- Tombstone orphan: `Warn` "[LedgerSwap] Orphan tombstone=<id>"
- Stray SupersedeTargetId on committed: `Warn` "[Recording] Stray SupersedeTargetId on committed rec=<id>; treating as cleared"
- Load summary: `Info` "[Rewind] Load sweep: zombies=<N> provRPs=<M> journalFinisher=<b> orphans=<s/t> rpsLoaded=<L> supersedesLoaded=<S>"

### 10.8 MergeJournal
- Begin: `Info` "[MergeJournal] sess=<sid> phase=Begin"
- Finisher triggered: `Info` "[MergeJournal] Finisher sess=<sid> markerWas=<present|absent>"
- Cleared: `Verbose` "[MergeJournal] sess=<sid> cleared"

---

## 11. Test Plan

### 11.1 Unit

- RP round-trip (incl. both PID maps, ChildSlots, session-prov state).
- Recording round-trip (MergeState tri-state, CreatingSessionId, transient SupersedeTargetId).
- RecordingSupersedeRelation round-trip.
- LedgerTombstone round-trip.
- ReFlySessionMarker round-trip + validation table.
- MergeJournal round-trip.
- Legacy migrations (MergeState, TerminalKind, ActionId) — idempotent, deterministic.
- `IsVisible(r)` under chain-length 0, 1, 2, 3.
- `IsUnfinishedFlight` table-driven (both BGCrash and Crashed terminals).
- `EffectiveRecordingId` forward walk with cycle-guard and orphan-terminator.
- ERS + ELS filter semantics: ELS filters ONLY by tombstones; verify contract/milestone/facility actions survive supersede of their recording.
- v1 tombstone-eligible type classifier.
- SessionSuppressedSubtree closure: forward-only + mixed-parent halt.

### 11.2 Integration

- **Full supersede round-trip.** BG-crash AB, land re-fly AB', merge. Assert relation in RecordingSupersedes; AB unchanged; IsVisible(AB)=false; ERS excludes AB; ELS still includes non-eligible AB actions; KerbalDeath retired via tombstone.
- **Crashed re-fly extends chain.** BG-crash AB, re-fly AB' crashes, merge. Assert AB superseded by AB'; AB' is CommittedProvisional Crashed; AB' is a new Unfinished Flight (predicate satisfied). Re-fly again to AB''; merge Landed. Assert chain AB -> AB' -> AB''; AB and AB' hidden; AB'' effective; RP reaps.
- **Subtree supersede.** BG-crash AB with orphan descendant d; supersede with AB'; both AB and d get supersede relations.
- **Mixed-parent halt.** Descendant c with parents {AB, externalRec}; closure does NOT include c; c stays visible.
- **Contract sticky across supersede.** BG-crash completed contract X; re-fly merged; ContractComplete NOT tombstoned; X still complete per ELS.
- **KerbalDeath tombstone + reservation re-derive.**
- **Null-scoped deadline-ContractFail sticks.** v1 does not retire it. Still in ELS.
- **RP reap on last-slot-Immutable.**
- **Tree discard purges everything** including supersede relations and tombstones within the tree.
- **Scenario save/load round-trip** with full entity set.
- **F5 mid-re-fly + quit + load.** Marker validates; all session-tagged recordings + RPs spared.
- **Merge journal crash-recovery matrix** (5 crash-point cases per §6.6 matrix).
- **Strip authoritative match** via PidSlotMap + fallback via RootPartPidMap.
- **Strip ghost-ProtoVessel guard.**
- **Strip leaves unrelated committed vessel alone.**
- **Orphan supersede relation not deleted** on load; walk treats as terminator.

### 11.3 Log assertion
- Every §10 decision point has a log-present test.
- "All other types sticky" summary logged on merge with non-empty tombstone-ineligible breakdown.
- Session suppression logged once per session.

### 11.4 Edge-case tests (one per §7 entry; select list)
7.3, 7.4, 7.5, 7.6, 7.10, 7.11, 7.12, 7.13 (contract sticky), 7.14 (contract-fail stays), 7.16 (kerbal recovery), 7.17 (crashed-merge re-rewindable), 7.38 (ghost guard), 7.39 (unrelated vessel), 7.40 (mixed-parent halt), 7.41 (null-scoped sticky), 7.43 (chain extends), 7.44 (vessel-destruction rep stays), 7.45 (journal recovery), 7.46 (walk direction + orphan), 7.47 (orphan not deleted), 7.48 (Immutable seals slot).

### 11.5 In-game (Ctrl+Shift+T)
- **CaptureRPOnStaging** — assert both PID maps populated, quicksave in Parsek/RewindPoints.
- **SavePathRootThenMove** — no stray file in save root.
- **InvokeRPStripAndActivate** — strip counts; active vessel PID unchanged.
- **MergeLandedReFlyCreatesImmutableSupersede** — relation in list, AB unchanged, AB' Immutable, RP reaps.
- **MergeCrashedReFlyCreatesCPSupersede** — relation in list, AB' CommittedProvisional Crashed, is an Unfinished Flight, RP does NOT reap.
- **ReRewindExtendsChain** — after merged crash, invoke RP again; strip uses PidSlotMap; new provisional created; chain extends on next merge.
- **ContractStickyAcrossSupersede** — contract completed by BG-crash; re-fly merges; contract still complete in KSP's `ContractSystem`; rep unchanged.
- **GhostSuppressionDuringReFly** — no ghost rendering for supersede-target subtree.
- **KerbalRecoveryOnSupersede** — kerbal returns active; reservation re-derived.
- **UnfinishedFlightsRenderingAndNoHide.**
- **WarpZeroedDuringSave.**
- **F5MidReFlyResume** — atomic phase 1+2; marker validates; spare set works.
- **MergeInterruptionRecovery** — inject exception at step 4 and step 9; verify finisher behaviour.
- **JournalFinisherMarkerPresentVariant** — inject exception between step 8 and step 11; reload; finisher runs with marker present.

### 11.6 Performance
- Quicksave write < 500 ms.
- Strip < 100 ms for 10 vessels.
- ERS + ELS compute < 5 ms for 100 recordings / 10k actions (cached; recomputed only on state change).
- Supersede merge + recalc < 200 ms.

### 11.7 Grep-audit (CI gate)
- Static: no file outside §3.4 consumer table references `RecordingStore.CommittedRecordings` or `Ledger.Actions` directly. Pattern `\.(CommittedRecordings|Actions)\b` outside approved list fails CI.

---

## 12. Implementation Sequencing

1. Data model + legacy migration (incl. ActionId, tri-state MergeState, both PID maps).
2. ERS/ELS shared utility (tombstone-only ELS filter).
3. Grep-audit conversion phase: convert existing raw consumers (GhostMapPresence, tracking station, watch mode, diagnostics, UI) to ERS/ELS. CI gate added.
4. RP creation + deferred quicksave + scene guard + warp-to-0 + root-save-then-move.
5. Unfinished Flights UI (read-only; cannot hide).
6. Rewind invocation: reconciliation + strip + activate + atomic phase 1+2 marker/provisional.
7. SessionSuppressedSubtree wiring (ghost walker + claim + GhostMapPresence + WatchModeController).
8. Merge: supersede relations + subtree closure.
9. Merge: v1 tombstone-eligible scope + LedgerTombstones + advisory logs.
10. Merge: journaled staged commit (Durable 1/2/3) + finisher on load.
11. RP reap + tree discard purge (including supersede relations confined to tree).
12. Revert-during-re-fly dialog.
13. Load-time sweep: journal finisher + marker validation + spare set + zombie cleanup + orphan log + stray-field log.
14. Polish: diagnostics disk display, rename/hide warnings, dialog copy, logs.

Phases 1-5 ship as "feature preview" (no gameplay change). Phase 6+ unlocks.

---

## 13. Open Questions / Deferred

- **OnSave re-entrancy** — mitigated by one-frame defer; monitor via telemetry.
- **Broader v2 supersede scope** — candidate ledger action types beyond KerbalDeath: vessel-destruction rep, science-loss on destruction. Each needs confirmation that KSP-side state can either be re-emitted or is purely Parsek-owned.
- **Cross-recording visual divergence** for shared stock vessels — freeze stock state at first Parsek-involved interaction.
- **Cross-tree dock bidirectional registry.**
- **Nested re-fly sessions** — v1 bans; v2 could stack markers.
- **Auto-purge policies for old reap-eligible RPs** — none in v1; monitor via disk-usage.
- **Merge-dialog copy polish.**
- **Physics easing** on active-vessel quicksave restore — existing VesselSpawner path; verify no regression.
- **Content-hash quicksave dedup** for simultaneous splits.
- **Recovery-snapshot feature** — SplitTimeSnapshot reinstated with a concrete recovery mechanism for corrupt quicksaves. Deferred.
- **Player-selectable merge mode for crashes** — v1 auto-picks CommittedProvisional for crash outcomes. v2 could offer "commit as final (no further rewind)" for players who want to seal the slot.

---

*End of design v0.5.*
