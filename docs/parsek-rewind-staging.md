# Parsek — Rewind to Staging

*Post-implementation design specification for the mid-mission rewind system that shipped in v0.9.0. Covers Rewind Points captured at multi-controllable split events (staging, undocking, EVA), the Unfinished Flights virtual group, the append-only supersede relation, narrow kerbal-death tombstone scope, the journaled staged commit, and the load-time sweep that keeps half-finished state bounded.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to an immutable timeline, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder, timeline, and ledger systems with Rewind-to-Staging. It assumes familiarity with the recording DAG, BranchPoint model, controller identity, ghost chains, and the additive-only invariant (see `parsek-flight-recorder-design.md`) and with the ledger model, immutable ActionId, reservations, and career-state replay (see `parsek-game-actions-and-resources-recorder-design.md`).*

**Status:** shipped in v0.9.0.
**Pre-implementation spec:** `docs/parsek-rewind-staging-design.md` (archived as-is). This document supersedes it as the source of truth for what actually shipped.
**Related docs:** `parsek-flight-recorder-design.md`, `parsek-timeline-design.md`, `parsek-game-actions-and-resources-recorder-design.md`, `parsek-logistics-routes-design.md`.

---

## 1. Introduction

A Parsek career is a committed timeline of missions. The player flies, commits, the ghost plays back, and the next mission begins on top. Before v0.9 the timeline had a sharp edge: once a multi-controllable split happened — a stage decoupled with a probe core on each side, a lander undocked from a station, a kerbal popped out on EVA — whichever half the player did not personally fly was recorded in the background and, if things went badly, committed as a crashed or destroyed sibling. There was no in-game path back to re-fly that half. The successful side was locked in with the failed side, and the only "fix" was to discard the entire tree and re-fly the whole mission.

Rewind to Staging is the narrow feature that unsticks that edge. At every split that produces two or more controllable entities, Parsek writes a transient KSP quicksave — a **Rewind Point** — plus a compact persistent-id-to-slot table captured at save time. If any sibling ends badly (destroyed, BG-crashed, or even just stranded in a state the player wants to redo), the recording appears in a read-only **Unfinished Flights** group in the Recordings Manager. Clicking Rewind on an Unfinished-Flight row reloads the quicksave, strips the non-selected siblings (they play back as ghosts from their committed recordings), and hands the player the other half at the exact split moment. When the re-fly ends and the player merges, the new recording supersedes the old one via an **append-only relation** — the original recording is never mutated or deleted, the ghost/claim subsystems just filter it out.

### 1.1 Scope

The v1 feature covers:

- **Split detection** at joint-break, undock, and EVA boundaries. `SegmentBoundaryLogic.IsMultiControllableSplit` (`Source/Parsek/SegmentBoundaryLogic.cs`) is the gate; debris-only splits and single-controllable splits do not receive a Rewind Point.
- **Rewind Point capture.** `RewindPointAuthor.Begin` (`Source/Parsek/RewindPointAuthor.cs:52`) synchronously attaches the RP to its `BranchPoint` and to `ParsekScenario.RewindPoints`, then a one-frame-deferred coroutine drops warp to zero, populates both PID-to-slot maps from live vessels, writes a stock KSP save, and atomically moves the result into `saves/<save>/Parsek/RewindPoints/<rpId>.sfs`.
- **Unfinished Flights UI group.** A virtual, read-only group in the Recordings Manager (`Source/Parsek/UI/UnfinishedFlightsGroup.cs`) computed per-frame from ERS filtered by `EffectiveState.IsUnfinishedFlight`. Membership updates automatically as flights are flown and merged.
- **Invocation.** `RewindInvoker` (`Source/Parsek/RewindInvoker.cs`) runs a five-precondition gate, captures a pre-load reconciliation bundle, copies the RP quicksave to the save-root (KSP's `LoadGame` does not accept subdirectory paths), triggers `GamePersistence.LoadGame` + `HighLogic.LoadScene(FLIGHT)`, then on the reloaded scene atomically runs Restore → Strip → Activate → provisional + `ReFlySessionMarker` write.
- **Append-only supersede.** On merge, `SupersedeCommit.AppendRelations` (`Source/Parsek/SupersedeCommit.cs:108`) appends one `RecordingSupersedeRelation` per recording in the forward-only merge-guarded subtree closure of the retired sibling. No field on a committed Recording is mutated post-commit.
- **Narrow v1 tombstone scope.** The only ledger retirement on supersede is `KerbalAssignment`-to-Dead actions and `ReputationPenalty` actions bundled with them (paired by same-recording kerbal-death within a 1-second UT window). Contract completions, milestones, facility upgrades, strategies, tech research, science rewards, funds spending, and vessel-destruction rep penalties all remain in the Effective Ledger Set.
- **Crashed re-fly stays rewindable.** `TerminalKindClassifier.Classify` (`Source/Parsek/TerminalKindClassifier.cs`) maps the provisional's terminal state to `Landed` (commits `Immutable`), `Crashed` (commits `CommittedProvisional` so the slot stays an Unfinished Flight), or `InFlight` (same as Landed).
- **Journaled staged commit.** `MergeJournalOrchestrator.RunMerge` (`Source/Parsek/MergeJournalOrchestrator.cs:149`) drives the merge through nine phase checkpoints, all reflected in `MergeJournal.Phase` (`Source/Parsek/MergeJournal.cs:53`). A load-time finisher rolls back (pre-Durable-1 phases) or drives to completion (post-Durable-1 phases).
- **Load-time sweep.** `LoadTimeSweep.Run` (`Source/Parsek/LoadTimeSweep.cs:51`) validates the re-fly marker's six durable fields via `MarkerValidator.Validate` (`Source/Parsek/MarkerValidator.cs:86`), discards zombie NotCommitted provisionals and session-provisional RPs not referenced by a valid marker, warn-logs orphan supersede/tombstone rows, and clears stray `SupersedeTargetId` fields.
- **Revert-during-re-fly dialog.** `RevertInterceptor` (`Source/Parsek/RevertInterceptor.cs`) prefixes `FlightDriver.RevertToLaunch` via Harmony and routes the player to `ReFlyRevertDialog` (`Source/Parsek/ReFlyRevertDialog.cs`) with three options: Retry from Rewind Point, Full Revert (Discard Re-fly), Continue Flying.

### 1.2 Out of scope

v1 deliberately does NOT attempt:

- Re-emitting retired contract completions, milestone flags, tech research, facility upgrades, strategies, or the KSP subsystems that own them (`ContractSystem`, `ProgressTracking`, `ScenarioUpgradeableFacilities`, `StrategySystem`, `ResearchAndDevelopment`). These are "sticky" — KSP will not re-emit a contract completion after `ContractSystem` marks it complete, so tombstoning the event and hoping the re-fly emits a fresh one produces zero credit. v1 sidesteps this by leaving all non-kerbal-death ledger actions in the Effective Ledger Set regardless of whether their source recording was superseded. See §2, §3, §8, §10 for the exact framing.
- Nested re-fly sessions. The precondition gate rejects a rewind invocation while another session is active (`RewindInvoker.cs:108`).
- Cross-tree supersedes. v1 does not produce them; `EffectiveState.EffectiveRecordingId` does not yet halt at cross-tree boundaries but has a TODO marker for when cross-tree supersedes become producible (see Known Limitations).
- Auto-purge policies for long-lived reap-eligible RPs. Monitoring via the disk-usage diagnostic is the v1 answer.
- A recovery-snapshot mechanism for corrupt quicksaves. The pre-impl `SplitTimeSnapshot` concept was dropped before v1 shipped; the KSP quicksave is the sole source of truth.

### 1.3 Who benefits

- Players who launch rockets with recoverable boosters or stages and want to actually land them after watching the upper stage reach orbit.
- Players who fly multi-vessel docking missions and want to re-do the sibling they did not focus on.
- Players whose kerbal died during an EVA that turned out to be avoidable, and who want to replay the EVA instead of eating the morale/reputation cost.
- Anyone who has ever said "I should have stayed on the other vessel."

### 1.4 Relationship to prior features

Rewind to Staging is layered on top of, not inside, the existing subsystems:

- **Flight recorder:** the recording DAG, segment boundary rule, controller identity, ghost chains, background recording, terminal kinds. Rewind to Staging does not change any of these. It adds new persistent state — Rewind Points, supersede relations, tombstones, a session marker, a journal — stored alongside the existing recording tree in `ParsekScenario`.
- **Timeline / ledger:** the immutable `ActionId`, the recalculation engine, the resource modules. The feature adds `GameAction.ActionId` as a hard precondition (legacy migration generates a deterministic hash on first load) and introduces `LedgerTombstone` as an append-only retirement filter, but the recalculation walk itself is unchanged — `LedgerOrchestrator.Recalculate*` now feeds from `EffectiveState.ComputeELS()` (the tombstone-filtered view) instead of raw `Ledger.Actions`.
- **Game actions & resources:** contracts, milestones, facilities, strategies, tech, science, funds, kerbals. v1 tombstones only kerbal deaths (plus bundled rep). Everything else sticks. This is the central design decision; see §2 and §10.

---

## 2. Design Philosophy

These principles governed every design and implementation decision. They are listed up front because they inform every section that follows.

### 2.1 Correct visually, minimal, efficient

Borrowed from the project-wide recording-design principle. A Rewind Point is 1–2 MB of quicksave. Many splits do not end in regret. Therefore: RP creation is cheap, deferred, and speculative; reap is eager (as soon as no slot can be re-flown, the file is deleted); cached ERS/ELS views rebuild only when source state changes; supersede filtering is a derived lookup, not a stored field.

### 2.2 Append-only history

The recording tree never shrinks. Supersede is a relation stored in a separate list, not a mutation on a recording. Tombstones are append-only. No field on a committed Recording is written after its `MergeState` flips out of `NotCommitted`. `EffectiveState.IsVisible` (`Source/Parsek/EffectiveState.cs:129`) is computed by walking `RecordingSupersedeRelation` entries; the Recording itself carries no `SupersededByRecordingId` field. Tree discard is the only path that ever removes supersede relations, and it removes the whole tree atomically.

### 2.3 Narrow v1 semantics

The central design decision. Supersede is a **physical-visibility / claim-tracker mechanism**, not a ledger or career-state eraser. The v1 tombstone-eligible type list is deliberately small: `KerbalAssignment`-to-Dead and the `ReputationPenalty` actions bundled with them. Everything else — contracts, milestones, facilities, strategies, tech, science, funds, vessel-destruction rep — remains in the Effective Ledger Set after supersede. This produces behavior the player can reason about: "my career state from the failed attempt stays; only the kerbals come back." It also produces behavior the mod can reliably deliver, because re-emitting retired KSP events is not generally possible.

### 2.4 Crash-recoverable merge

Every staged-commit merge step is journaled through `MergeJournal.Phase` (`Source/Parsek/MergeJournal.cs:53`). Nine phase strings mark the merge's progress; on load, the finisher reads the phase and either rolls back (pre-Durable-1 phases: disk still holds the pre-merge snapshot, in-memory changes evaporate) or drives to completion (post-Durable-1 phases: disk holds the merged state, the remaining steps resume). The journal IS the durability barrier; the next natural `ScenarioModule.OnSave` flushes each phase's in-memory state to disk. Tests inject a synchronous save stub to exercise every window (see `MergeCrashRecoveryMatrixTests.cs`).

### 2.5 Atomic provisional + marker write

The provisional re-fly Recording and its `ReFlySessionMarker` must land in the scenario in the same synchronous block; no KSP `OnSave` may capture the first without the second. `RewindInvoker.AtomicMarkerWrite` (`RewindInvoker.cs:463`) enforces this: the provisional is added, the marker is constructed and assigned, and a `try/catch` rolls back both if anything throws. There is no coroutine yield, no deferred save, and no frame gap between `CheckpointA:BeforeProvisional` and `CheckpointB:AfterMarker` — the `CheckpointHookForTesting` observer (`RewindInvoker.cs:47`) is the only thing that can see in between.

### 2.6 Player-visible opt-in

A re-fly never happens behind the player's back. It requires an explicit click on the Rewind button in the Unfinished Flights row; the confirmation dialog names the canonical semantics (`RewindInvoker.ShowDialog` body at `RewindInvoker.cs:157`). Rewind Points are written automatically on every multi-controllable split, but they are invisible until a sibling ends up Unfinished — the player sees the UI entry only when it is actionable.

### 2.7 Observable from logs alone

Every decision point in §6 and every RP / marker / journal / sweep / reap state transition emits a log line. The tag catalog in §12 is what a KSP.log reader needs to reconstruct a session's history: the `[Rewind]`, `[RewindSave]`, `[RewindUI]`, `[ReFlySession]`, `[Supersede]`, `[LedgerSwap]`, `[MergeJournal]`, `[LoadSweep]`, `[UnfinishedFlights]`, `[CrewReservations]`, `[RevertInterceptor]`, `[Recording]`, `[ReconciliationBundle]`, `[ERS]`, `[ELS]` tags appear exactly where §6 and §8 say they do.

---

## 3. Terminology

This section fixes the vocabulary. Throughout the doc, "recording" (lowercase) is the general concept; **Recording** (capitalized, code-font) is the class (`Source/Parsek/Recording.cs`). "Session" always means a Rewind-to-Staging re-fly session unless qualified.

- **Split event** — a `BranchPoint` whose type is `JointBreak`, `Undock`, or `EVA` (see `parsek-flight-recorder-design.md`). The common case is staging (joint break on the decoupler), but lander undocks and EVAs are the same shape.
- **Controllable entity** — a vessel with at least one `ModuleCommand` part, or an EVA kerbal. The classifier `SegmentBoundaryLogic.IsMultiControllableSplit` gates RP creation on `count >= 2`.
- **Multi-controllable split** — a split event that produces two or more controllable entities. Only these get a Rewind Point.
- **Rewind Point (RP)** — the durable object at a multi-controllable split. Holds the quicksave filename, the save UT, the ChildSlots, the PidSlotMap, the RootPartPidMap, plus bookkeeping fields. Defined in `Source/Parsek/RewindPoint.cs`. "Session-provisional" during re-fly; "persistent" after session merge.
- **Child slot** — a stable, ordered entry on an RP identifying one controllable output of the split. Carries `OriginChildRecordingId` (immutable) and derives `EffectiveRecordingId` via a forward walk through `RecordingSupersedeRelation`. Defined in `Source/Parsek/ChildSlot.cs`.
- **PidSlotMap** — `Dictionary<uint, int>` mapping `Vessel.persistentId` to `ChildSlot.SlotIndex`. Populated at quicksave-write time by `RewindPointAuthor.ExecuteDeferredBody` (`Source/Parsek/RewindPointAuthor.cs:267`). The authoritative primary match for the post-load strip.
- **RootPartPidMap** — `Dictionary<uint, int>` mapping root-part `Part.persistentId` to `ChildSlot.SlotIndex`. Populated at the same time as PidSlotMap. Fallback match when KSP has reassigned a vessel-level persistentId between save and load. `Part.persistentId` is the stable cross-save/load identity for a physical part; `Part.flightID` is session-scoped and unstable.
- **Finished recording** — a Recording with `MergeState == Immutable`.
- **Unfinished Flight** — a Recording matching `EffectiveState.IsUnfinishedFlight` (see §5). Visible in the ERS, terminal state classified as Crashed, parent BranchPoint carries a non-null `RewindPointId`, and a live RewindPoint entry exists for that BranchPoint.
- **Re-fly session** — the period between a Rewind Point invocation and its merge / discard. Identified by a `SessionId` GUID generated at invocation (`RewindInvoker.cs:230`). Ends on merge, discard, retry (new SessionId), full-revert, or load-time validation failure.
- **Session-provisional RP** — an RP whose `SessionProvisional` flag is `true`. Newly created RPs are session-provisional; the flag flips to `false` during the merge's `TagRpsForReap` step (`MergeJournalOrchestrator.cs:441`) for RPs whose `CreatingSessionId` matches the merging session. Load-time sweep discards session-provisional RPs that are not referenced by a valid marker.
- **Supersede** — an append-only relation `RecordingSupersedeRelation { RelationId, OldRecordingId, NewRecordingId, UT, CreatedRealTime }` stored in `ParsekScenario.RecordingSupersedes`. No Recording is mutated; `EffectiveState.IsVisible` filters via the list.
- **Supersede chain** — the forward walk through `RecordingSupersedes` from a `ChildSlot.OriginChildRecordingId`. Chains can extend through multiple re-fly attempts (AB → AB' → AB'' if AB' was merged as a crashed `CommittedProvisional`).
- **Session-suppressed subtree** — during an active re-fly, a second narrow carve-out applied to physical-visibility subsystems only (ghost walker, chain tip, map presence, watch mode). Forward-only closure from the marker's `OriginChildRecordingId`, halting at mixed-parent BranchPoints. Computed by `EffectiveState.ComputeSessionSuppressedSubtree` (called from `Source/Parsek/SessionSuppressionState.cs`). Career and ledger subsystems do NOT consume this view — they read ERS / ELS directly.
- **Tombstone** — an append-only `LedgerTombstone { TombstoneId, ActionId, RetiringRecordingId, UT, CreatedRealTime }` in `ParsekScenario.LedgerTombstones`. Keyed by the retired `GameAction.ActionId`. Multiple tombstones for the same ActionId are tolerated; the ELS filter is "at least one tombstone exists." Defined in `Source/Parsek/GameActions/LedgerTombstone.cs`.
- **ReFlySessionMarker** — the singleton marker persisted while a session is live. Seven fields total; six are validated on load (`MarkerValidator.Validate`), `InvokedRealTime` is informational and never fails a load. Defined in `Source/Parsek/ReFlySessionMarker.cs`.
- **Provisional re-fly Recording** — the live Recording created at invocation with `MergeState == NotCommitted`, `CreatingSessionId` set to the session GUID, `SupersedeTargetId` set to the retired sibling's id, `ProvisionalForRpId` set to the invoked RP's id. Becomes `Immutable` or `CommittedProvisional` at merge.
- **MergeJournal** — the singleton journal that carries the merge through nine phase strings (`Begin`, `Supersede`, `Tombstone`, `Finalize`, `Durable1Done`, `RpReap`, `MarkerCleared`, `Durable2Done`, `Complete`). Its presence on load triggers the finisher. Defined in `Source/Parsek/MergeJournal.cs`.
- **Effective Recording Set (ERS)** — the derived, read-only view of committed recordings that count right now. Computed by `EffectiveState.ComputeERS` (`Source/Parsek/EffectiveState.cs:236`). Filters out `NotCommitted`, superseded, and (during an active session) session-suppressed recordings.
- **Effective Ledger Set (ELS)** — the derived, read-only view of ledger actions that count right now. Computed by `EffectiveState.ComputeELS` (`EffectiveState.cs:310`). Filters out actions whose `ActionId` appears in any `LedgerTombstone`. **The ONLY filter on ELS is the tombstone check.** A superseded recording's non-eligible actions (contract completions, milestones, facility upgrades, etc.) remain in ELS.

---

## 4. Mental Model

The tree diagrams below use `(V)` for "visible in ERS" and `(H)` for "hidden (superseded)". `Immutable` is abbreviated `I`; `CommittedProvisional` is `CP`; `NotCommitted` is `NC`.

### 4.1 A mission with one staging split, one side crashes, player re-flies and lands

**Launch:** one vessel, `NotCommitted`, recording live.

```
ABC (NC, live)   (V)
```

**Staging:** `ABC` splits into upper stage `AB` (continues to orbit) and booster `A` (falls back). Both are controllable (probe cores on both). `SegmentBoundaryLogic.IsMultiControllableSplit` returns true; `RewindPointAuthor.Begin` creates RP-1 with two ChildSlots and schedules the deferred quicksave. On the next frame the quicksave is written and atomically moved into `Parsek/RewindPoints/rp_<guid>.sfs`; `PidSlotMap` and `RootPartPidMap` are populated from live vessels.

```
ABC (NC, terminates at split-1)   (V)
 |
 +-- [slot-0: AB]  (NC, live, focus)        (V)
 +-- [slot-1: A]   (NC, BG-recording)       (V)
     RP-1 (SessionProvisional, maps captured)
```

**Orbit + merge:** player flies AB to orbit, then returns to Space Center. Merge dialog appears. Booster A crashed in the background. On "Merge to Timeline" the recorder commits ABC and AB as `Immutable`; A's terminal kind classifies as `Crashed`, so its `MergeState` becomes `CommittedProvisional`. RP-1's slots: slot-0's effective recording is AB (`Immutable`), slot-1's effective recording is A (`CommittedProvisional`). RP-1 is NOT reap-eligible (slot-1 is still open), so the quicksave and scenario entry persist.

```
ABC (I)                                     (V)
 |
 +-- AB  (I, Orbited)                       (V)
 +-- A   (CP, Crashed)                      (V)  <- Unfinished Flight
     RP-1 persistent
     Unfinished Flights = {A}
```

`A` now satisfies `IsUnfinishedFlight`: visible, terminal kind classified as Crashed, parent BranchPoint has a live RP. The Recordings Manager shows it in the Unfinished Flights virtual group with a Rewind button.

**Invocation:** player clicks Rewind on row A. `RewindInvoker` runs through preconditions, captures the reconciliation bundle, copies the RP quicksave to the save-root as `Parsek_Rewind_<sessionId>.sfs`, triggers `GamePersistence.LoadGame` + `HighLogic.LoadScene(FLIGHT)`. On the reloaded scene, `ParsekScenario.OnLoad` calls `RewindInvoker.ConsumePostLoad`, which atomically:
1. Restores the pre-load reconciliation bundle over the quicksave-loaded state (recordings, supersedes, tombstones, reservations, etc. preserved).
2. Runs `PostLoadStripper.Strip(rp, slotIdx=1)` — AB is matched via `PidSlotMap` and stripped (`Vessel.Die()`); A is matched and kept; unrelated vessels are left alone; ghost ProtoVessels are skipped.
3. Calls `FlightGlobals.SetActiveVessel(A)`.
4. Creates the provisional re-fly Recording `A'` with `MergeState = NotCommitted`, `CreatingSessionId = sessionId`, `SupersedeTargetId = A.Id`, `ProvisionalForRpId = rp.Id`, and writes the `ReFlySessionMarker` — same synchronous block, no yield.
5. Recalculates the ledger via `LedgerOrchestrator.RecalculateAndPatch()`.

During the re-fly: ERS = `{ABC, AB, A}`. A' is `NotCommitted` so it is not in ERS. The ghost walker further filters ERS by `SessionSuppressedSubtree(marker)`, which contains `{A}` — so A does not render as a ghost. AB and ABC play back as ghosts. A' is the live active vessel, handled by the normal active-vessel path.

```
ABC (I)                                     (V)
 |
 +-- AB  (I)                                (V)  <- ghost playback
 +-- A   (CP, Crashed)                      (V)  <- in SessionSuppressedSubtree (invisible)
 +-- A'  (NC, live, CSId=sess1,             (V)  <- active vessel
          SupersedeTargetId=A)
     ReFlySessionMarker active
```

**Successful re-fly + merge:** player lands A' on the launch pad. Recording terminates with `TerminalKind = Landed` (via `TerminalKindClassifier`). Merge dialog fires `MergeJournalOrchestrator.RunMerge`. The orchestrator:
1. Writes the journal at `Phase = Begin`.
2. `SupersedeCommit.AppendRelations`: subtree closure for A is `{A}`; one `RecordingSupersedeRelation { OldRecordingId = A.Id, NewRecordingId = A'.Id, UT = now }` appended.
3. `SupersedeCommit.CommitTombstones`: scans ledger actions in-scope; no kerbal deaths in A's scope → zero tombstones.
4. `SupersedeCommit.FlipMergeStateAndClearTransient`: A' becomes `Immutable` (Landed). `SupersedeTargetId` cleared. Supersede state version bumped. Marker preserved for step 7.
5. Durable Save #1 (deferred — journal phase string is the durable barrier).
6. `TagRpsForReap`: RP-1's `SessionProvisional` was already false (from merge-1); its `CreatingSessionId` does not match sess1 (it was `null` when A's tree committed), so nothing is tagged here. `RewindPointReaper.ReapOrphanedRPs`: RP-1's slot-0 effective is AB (`Immutable`), slot-1's effective is A' (`Immutable`). Both slots are `Immutable` → RP-1 is reap-eligible. Quicksave file deleted, scenario entry removed, BranchPoint.RewindPointId cleared.
7. Marker cleared. Supersede state version bumped again.
8. Durable Save #2 (deferred).
9. Journal set to `Complete`, then cleared. Durable Save #3 (deferred).

After merge: A is hidden (superseded), A' is the effective representative of slot-1. Unfinished Flights is empty.

```
ABC (I)                                     (V)
 |
 +-- AB  (I)                                (V)
 +-- A   (CP, Crashed, SUPERSEDED)          (H)
 +-- A'  (I, Landed)                        (V)  effective slot-1
     RP-1 reaped (quicksave deleted)
     RecordingSupersedes: {A -> A'}
     Unfinished Flights = {}
```

### 4.2 Crashed re-fly stays rewindable (chain extension)

Same setup as §4.1 except A' also crashes. The player wants to try again.

On merge: `TerminalKindClassifier.Classify(A')` returns `Crashed`. `SupersedeCommit.FlipMergeStateAndClearTransient` assigns `A'.MergeState = CommittedProvisional`. Supersede relation `{A -> A'}` still appends. A is hidden; A' is the new effective slot-1 representative AND a new Unfinished Flight (it satisfies the predicate: visible, CP, Crashed, parent BP has RP-1).

```
ABC (I)                                     (V)
 |
 +-- AB  (I)                                (V)
 +-- A   (CP, Crashed, SUPERSEDED)          (H)
 +-- A'  (CP, Crashed)                      (V)  <- NEW Unfinished Flight
     RP-1 still persistent (slot-1 still open)
     RecordingSupersedes: {A -> A'}
     Unfinished Flights = {A'}
```

The player invokes RP-1 again. `PidSlotMap` still carries the original vessels' PIDs from the quicksave — that's the same quicksave, nothing about it changed. After strip + activate, a new provisional `A''` is created with `SupersedeTargetId = A'.Id` (note: targets `A'`, not `A`; the UI's "effective recording for slot-1" is A' now).

A'' lands safely. Merge. `TerminalKind = Landed`, so `A''.MergeState = Immutable`. Supersede relation `{A' -> A''}` appends. The chain is now `A -> A' -> A''`; `ChildSlot.EffectiveRecordingId(supersedes)` walks forward: start at A, find `{A -> A'}`, step to A', find `{A' -> A''}`, step to A'', no further relation → return A''. Slot-1's effective recording is A''. Both slots immutable → RP-1 reap-eligible → quicksave deleted, scenario entry removed.

```
ABC (I)                                     (V)
 |
 +-- AB  (I)                                (V)
 +-- A   (CP, H)                            (H)
 +-- A'  (CP, H)                            (H)
 +-- A'' (I, Landed)                        (V)  effective slot-1
     RP-1 reaped
     RecordingSupersedes: {A -> A'}, {A' -> A''}
     Unfinished Flights = {}
```

Key invariants shown:

1. The tree never shrinks. Every provisional stays around; only relations accumulate.
2. Supersede chains can extend arbitrarily through crashed re-flies.
3. Only `Immutable` closes the rewind opportunity; `CommittedProvisional` keeps the slot rewindable.
4. Career state reads ELS, which is unchanged by supersede in this scenario (no kerbal deaths).

### 4.3 Session-suppressed subtree with mixed-parent halt

The session-suppressed subtree is the physical-visibility carve-out applied during an active re-fly. It is a **forward-only** walk from the marker's `OriginChildRecordingId`, stopping at any descendant whose parents include a recording outside the subtree.

Suppose mission `ABC` decouples into `AB` and `C` (split-1). `AB` then decouples into `A` and `B` (split-2). Later, `B` docks with a station `S` from an unrelated tree; the dock merges B with S into a new recording `BS`.

```
ABC
 |
 +-- AB
 |    |
 |    +-- A
 |    +-- B ----+
 |              |
 +-- C          v
               BS  (parents = {B, S-tree-tip})
 S-tree:
   S (own lineage)
```

Player invokes RP at split-1 targeting C (C crashed). SessionSuppressedSubtree is the forward closure of `OriginChildRecordingId = C.Id`, which is just `{C}` because C has no descendants — that's fine.

Now suppose the player invokes RP at split-1 targeting AB (AB crashed — hypothetical). The closure starts at AB, walks to its descendants {A, B}, and would then consider BS. But BS has parents `{B, S-tree-tip}`, and `S-tree-tip` is NOT in the subtree — the closure halts here. `BS` stays visible in ERS during the session; the mixed-parent constraint means a descendant that has any outside parent is not session-suppressed. Without this halt, suppressing BS would also suppress S-tree activity that the player may be relying on.

---

## 5. Data Model

Every class in this section lives in `Source/Parsek/`. ConfigNode VALUE keys are listed alongside the node name so the save-file layout is self-documenting.

### 5.1 MergeState

File: `Source/Parsek/MergeState.cs`.

```csharp
public enum MergeState
{
    NotCommitted = 0,
    CommittedProvisional = 1,
    Immutable = 2
}
```

Tri-state replacement of the legacy binary `committed` flag. Legacy saves migrate on load: `committed = True` → `Immutable`; `committed = False` → `NotCommitted`; absent field → `Immutable` (the pre-feature invariant — every recording reachable from a committed tree was already sealed). Serialized as `mergeState = <value>` on the RECORDING node.

- `NotCommitted`: recording is still being produced (recorder live, or re-fly provisional with `SupersedeTargetId` set).
- `CommittedProvisional`: session merge has stamped the recording but the rewind slot remains available (BG-recorded children under an RP; re-fly that ended in a crash and is still re-rewindable).
- `Immutable`: recording is sealed. Never mutated after this point; the rewind slot is closed.

### 5.2 Recording additions

File: `Source/Parsek/Recording.cs:193–208`.

| Field | Type | Purpose |
|---|---|---|
| `MergeState` | `MergeState` | Tri-state commit state (§5.1). |
| `CreatingSessionId` | `string` | Session GUID for recordings produced during an active re-fly. `null` outside sessions. Used by the load-time spare-set logic (§8.9) when a session crashed. |
| `SupersedeTargetId` | `string` | Transient — set only on `NotCommitted` provisional re-fly recordings to signal the intended supersede target. Cleared at merge when a concrete `RecordingSupersedeRelation` is appended. A non-empty value on `Immutable` / `CommittedProvisional` recordings triggers a Warn log and is treated as cleared (load-path legacy safety; see §8.9 step 5 and `LoadTimeSweep.ClearStraySupersedeTargets`). |
| `ProvisionalForRpId` | `string` | Back-pointer to the invoked RP's id. Set at invocation by `RewindInvoker.BuildProvisionalRecording` (`RewindInvoker.cs:527`). Null outside sessions. |

`SupersededByRecordingId` is **NOT** a field on Recording. Visibility is a derived lookup through `RecordingSupersedeRelation`.

### 5.3 BranchPoint additions

File: `Source/Parsek/BranchPoint.cs:47`.

| Field | Type | Purpose |
|---|---|---|
| `RewindPointId` | `string` | The id of the RP attached to this BranchPoint, or `null` if no RP was written (fewer than two controllable children, scene-guard abort, save failure). Stamped synchronously by `RewindPointAuthor.Begin` (`RewindPointAuthor.cs:138`). |

Serialized as `rewindPointId = <value>` on the BRANCH_POINT node when non-null.

### 5.4 GameAction additions

File: `Source/Parsek/GameActions/GameAction.cs`.

| Field | Type | Purpose |
|---|---|---|
| `ActionId` | `string` | Stable, immutable `act_<Guid-N>` id. Precondition for tombstoning. Legacy migration at load generates a deterministic hash from `UT + Type + RecordingId + Sequence` (`GameAction.cs:541`), so the same legacy action always yields the same id on repeated loads — tombstones written on one load resolve cleanly on the next. |

Migration log on first-post-upgrade load: `[LegacyMigration] Info: Assigned deterministic ActionId to <N> legacy actions`. Idempotent — replaying the migration on a save that already carries ActionIds is a no-op.

### 5.5 RewindPoint

File: `Source/Parsek/RewindPoint.cs`.

| Field | Type | Purpose |
|---|---|---|
| `RewindPointId` | `string` | Stable id in the format `rp_<Guid-N>`. Must satisfy `RecordingPaths.ValidateRecordingId`. |
| `BranchPointId` | `string` | Weak link back to the `BranchPoint`. |
| `UT` | `double` | `Planetarium.UT` at quicksave write. |
| `QuicksaveFilename` | `string` | Relative path under the save dir; format `Parsek/RewindPoints/<rpId>.sfs`. |
| `ChildSlots` | `List<ChildSlot>` | One entry per controllable sibling at split time. |
| `PidSlotMap` | `Dictionary<uint, int>` | `Vessel.persistentId` → `ChildSlot.SlotIndex`. Primary match for post-load strip. |
| `RootPartPidMap` | `Dictionary<uint, int>` | Root `Part.persistentId` → `ChildSlot.SlotIndex`. Fallback when KSP reassigned a vessel persistentId between save and load. |
| `SessionProvisional` | `bool` | `true` while the creating session still owns the RP; flips `false` at `TagRpsForReap` during merge. |
| `Corrupted` | `bool` | `true` when quicksave validation failed (file missing at deferred save, all slots undetectable, PartLoader deep-parse failed). RP kept for diagnostic visibility; Rewind button disabled. |
| `CreatingSessionId` | `string` | Session GUID when the RP was created inside an active session; `null` otherwise. |
| `CreatedRealTime` | `string` | Wall-clock ISO-8601 UTC timestamp. |

Node layout (emitted by `RewindPoint.SaveInto`, `RewindPoint.cs:75`):

```
POINT
{
    rewindPointId = rp_...
    branchPointId = bp_...
    ut = 1742538.43
    quicksaveFilename = Parsek/RewindPoints/rp_...sfs
    sessionProvisional = True|False
    corrupted = True             # omitted when False
    creatingSessionId = sess_... # omitted when empty
    createdRealTime = 2026-04-17T21:35:12Z

    CHILD_SLOT { slotIndex = 0  originChildRecordingId = rec_... controllable = True }
    CHILD_SLOT { slotIndex = 1  originChildRecordingId = rec_... controllable = True }

    PID_SLOT_MAP      { pid = 12345678  slot = 0 }
    PID_SLOT_MAP      { pid = 87654321  slot = 1 }

    ROOT_PART_PID_MAP { pid = 11223344  slot = 0 }
    ROOT_PART_PID_MAP { pid = 99887766  slot = 1 }
}
```

### 5.6 ChildSlot

File: `Source/Parsek/ChildSlot.cs`.

| Field | Type | Purpose |
|---|---|---|
| `SlotIndex` | `int` | Zero-based position within `RewindPoint.ChildSlots`. |
| `OriginChildRecordingId` | `string` | Immutable id of the recording originally created for this slot at split time. |
| `Controllable` | `bool` | Reserved for future classifier churn; always `true` in v1 (only controllable entities produce slots). |
| `Disabled` | `bool` | `true` if the Rewind button for this slot must be grayed out (PidSlotMap lookup failed for this slot at split time, or a loader sanity check failed). |
| `DisabledReason` | `string` | Human-readable reason shown in the UI tooltip. Current reasons: `no-live-vessel`. |

Derived:
- `EffectiveRecordingId(IReadOnlyList<RecordingSupersedeRelation>)` delegates to `EffectiveState.EffectiveRecordingId` (`EffectiveState.cs:78`). Forward walk from `OriginChildRecordingId`, cycle-guarded via visited set, returns the last-reached id when no further relation matches.

Node layout:

```
CHILD_SLOT
{
    slotIndex = 0
    originChildRecordingId = rec_...
    controllable = True
    disabled = True              # omitted when False
    disabledReason = no-live-vessel  # omitted when null
}
```

### 5.7 RecordingSupersedeRelation

File: `Source/Parsek/RecordingSupersedeRelation.cs`.

| Field | Type | Purpose |
|---|---|---|
| `RelationId` | `string` | Stable id in the format `rsr_<Guid-N>`. |
| `OldRecordingId` | `string` | The superseded recording (stays in `RecordingStore`, hidden from ERS). |
| `NewRecordingId` | `string` | The superseding recording (the re-fly provisional promoted on merge). |
| `UT` | `double` | `Planetarium.UT` at which the merge occurred. |
| `CreatedRealTime` | `string` | Wall-clock ISO-8601 UTC. |

Stored in `ParsekScenario.RecordingSupersedes`. Append-only. Never mutated. Removed only by whole-tree discard (`TreeDiscardPurge.PurgeTree`, §6.11). Orphan relations (rare; endpoint missing from `RecordingStore`) stay in place with a Warn log on every load (`LoadTimeSweep.cs:274`); the forward walk in `EffectiveState.EffectiveRecordingId` handles an unresolved `NewRecordingId` as a chain terminator.

Node layout (emitted as `ENTRY` children of the `RECORDING_SUPERSEDES` parent node):

```
ENTRY
{
    relationId = rsr_...
    oldRecordingId = rec_...
    newRecordingId = rec_...
    ut = 1742800.00
    createdRealTime = 2026-04-17T22:10:00Z
}
```

### 5.8 LedgerTombstone

File: `Source/Parsek/GameActions/LedgerTombstone.cs`.

| Field | Type | Purpose |
|---|---|---|
| `TombstoneId` | `string` | Stable id in the format `tomb_<Guid-N>`. |
| `ActionId` | `string` | The superseded action's immutable `GameAction.ActionId`. Aliases the design-spec's `SupersededActionId`; the code uses `ActionId` to match the field it refers to. |
| `RetiringRecordingId` | `string` | The new recording whose merge caused this tombstone to be written. |
| `UT` | `double` | `Planetarium.UT` at which the merge occurred. |
| `CreatedRealTime` | `string` | Wall-clock ISO-8601 UTC. |

Stored in `ParsekScenario.LedgerTombstones`. Append-only. Multiple tombstones for the same `ActionId` are tolerated — the ELS filter is "at least one tombstone exists." Removed only by whole-tree discard when the underlying action's recording is in the discarded tree.

Node layout (emitted as `ENTRY` children of `LEDGER_TOMBSTONES`):

```
ENTRY
{
    tombstoneId = tomb_...
    actionId = act_...
    retiringRecordingId = rec_...
    ut = 1742800.00
    createdRealTime = 2026-04-17T22:10:00Z
}
```

### 5.9 ReFlySessionMarker

File: `Source/Parsek/ReFlySessionMarker.cs`.

Singleton marker present in `ParsekScenario` iff a re-fly session is live. Seven fields; six are validated by `MarkerValidator.Validate`; the seventh (`InvokedRealTime`) is informational and never fails a load.

| Field | Validated? | Purpose |
|---|---|---|
| `SessionId` | yes | Unique GUID per invocation / retry. |
| `TreeId` | yes | RecordingTree this re-fly belongs to. Must exist in `RecordingStore.CommittedTrees`. |
| `ActiveReFlyRecordingId` | yes | The `NotCommitted` provisional re-fly recording. Must resolve in `CommittedRecordings` with `MergeState == NotCommitted`. |
| `OriginChildRecordingId` | yes | The supersede target (the retired sibling). Must resolve in `CommittedRecordings`. |
| `RewindPointId` | yes | The invoked RP's id. Must resolve in `ParsekScenario.RewindPoints`. |
| `InvokedUT` | yes | `Planetarium.UT` at invocation. Must not be strictly greater than the current UT. |
| `InvokedRealTime` | **no** | Wall-clock ISO-8601 UTC. Informational; purely for human-readable logs and diagnostics. |

Persisted as a single `REFLY_SESSION_MARKER` ConfigNode on `ParsekScenario`:

```
REFLY_SESSION_MARKER
{
    sessionId = sess_...
    treeId = tree_...
    activeReFlyRecordingId = rec_...
    originChildRecordingId = rec_...
    rewindPointId = rp_...
    invokedUT = 1742800.00
    invokedRealTime = 2026-04-17T22:10:00Z
}
```

Cleared on: re-fly merge (after Durable Save #1), re-fly discard (return-to-Space-Center), retry (a fresh marker with a new `SessionId` replaces the old one), full-revert (tree discard clears it if scoped to the discarded tree), load-time validation failure (cleared + Warn-logged).

### 5.10 MergeJournal

File: `Source/Parsek/MergeJournal.cs`.

Singleton journal present on `ParsekScenario` only during a staged-commit merge.

| Field | Type | Purpose |
|---|---|---|
| `JournalId` | `string` | Per-run id `mj_<Guid-N>`. Addition to the pre-impl design spec for log readability. |
| `SessionId` | `string` | Merge session GUID (matches the retiring marker's `SessionId`). |
| `Phase` | `string` | One of the `Phases` constants (below). |
| `StartedUT` | `double` | `Planetarium.UT` at merge start. |
| `StartedRealTime` | `string` | Wall-clock ISO-8601 UTC. |

The `Phases` vocabulary (`MergeJournal.cs:53`):

| Phase string | What has happened on disk | Recovery on load |
|---|---|---|
| `Begin` | Journal written. Nothing else. | Rollback. |
| `Supersede` | Supersede relations half-written or fully written in memory. | Rollback. |
| `Tombstone` | Tombstones half-written or fully written in memory. | Rollback. |
| `Finalize` | MergeState flipped + transient cleared in memory. | Rollback. |
| `Durable1Done` | First durable save flushed: supersedes + tombstones + MergeState on disk. Marker + RPs still live. | Complete from here (tag RPs → reap → clear marker → …). |
| `RpReap` | Session-prov RPs tagged + reaped. Marker still live. | Complete (clear marker → …). |
| `MarkerCleared` | Marker cleared in memory. | Complete (Durable Save #2). |
| `Durable2Done` | Second durable save flushed. | Complete (clear journal → Durable Save #3). |
| `Complete` | Journal set to `Complete`; Durable Save #3 interrupted before clear. | Idempotent clear. |

`MergeJournal.IsPreDurablePhase(phase)` returns true for `Begin` / `Supersede` / `Tombstone` / `Finalize`; `IsPostDurablePhase` returns true for `Durable1Done` / `RpReap` / `MarkerCleared` / `Durable2Done` / `Complete`.

Persisted as a single `MERGE_JOURNAL` ConfigNode on `ParsekScenario`:

```
MERGE_JOURNAL
{
    journalId = mj_...
    sessionId = sess_...
    phase = Begin
    startedUT = 1742800.00
    startedRealTime = 2026-04-17T22:10:00Z
}
```

### 5.11 ParsekScenario additions

The scenario module owns five new persistent collections, each wrapped in its own parent node on save (`ParsekScenario.OnSave`; verified at `Source/Parsek/ParsekScenario.cs:716–825`):

```
PARSEK
{
    ...
    REWIND_POINTS        { POINT { ... } POINT { ... } ... }
    RECORDING_SUPERSEDES { ENTRY { ... } ENTRY { ... } ... }
    LEDGER_TOMBSTONES    { ENTRY { ... } ENTRY { ... } ... }
    REFLY_SESSION_MARKER { ... }  # present only during active session
    MERGE_JOURNAL        { ... }  # present only during staged-commit merge
    ...
}
```

All five sections are additive — a save written before v0.9 simply has none of them, and `OnLoad` treats the absence as empty lists / null singletons. Saves written by v0.9 and loaded on v0.8.x would fail cleanly because the pre-v0.9 scenario does not know these keys; no downgrade path is provided.

Runtime-only state counters on the scenario drive the ERS / ELS cache invalidation:

| Counter | Bumped by |
|---|---|
| `SupersedeStateVersion` | Every mutation to `RecordingSupersedes`, marker identity change, MergeState flip. |
| `TombstoneStateVersion` | Every mutation to `LedgerTombstones`. |
| `RecordingStore.StateVersion` | Every mutation to `CommittedRecordings`. |
| `Ledger.StateVersion` | Every mutation to `Actions`. |

### 5.12 Directory layout

```
saves/<save>/
    persistent.sfs                             (KSP standard)
    Parsek/
        Recordings/
            <recordingId>.prec                 (existing, unchanged)
            <recordingId>_vessel.craft         (existing, unchanged)
            <recordingId>_ghost.craft          (existing, unchanged)
            <recordingId>.pcrf                 (existing, unchanged)
        RewindPoints/
            <rewindPointId>.sfs                (NEW; KSP-format quicksave)
    Parsek_Rewind_<sessionId>.sfs              (NEW; transient, root-level)
```

The transient `Parsek_Rewind_<sessionId>.sfs` at save-root exists only between `RewindInvoker.StartInvoke` (which copies `Parsek/RewindPoints/<rpId>.sfs` to this location) and the post-load cleanup in `RewindInvoker.ConsumePostLoad` (`RewindInvoker.cs:453`). The copy is required because `GamePersistence.LoadGame(fileName, folder, ...)` does not accept subdirectory paths. The cleanup runs in a `finally` block regardless of success, so a crash mid-invocation leaves at most one stale copy that the next invocation overwrites.

### 5.13 Unfinished Flights virtual UI group

File: `Source/Parsek/UI/UnfinishedFlightsGroup.cs`.

Not stored in `GroupHierarchyStore`. Membership derived per frame from ERS filtered by `EffectiveState.IsUnfinishedFlight`. As a system group it **cannot be hidden** (§9.4 / §7.30) and **cannot be a drop target** for manual group assignment (§9.5 / §7.25). Rename on an individual member row still persists through the standard `Recording.VesselName` path; hide on a member row warns and refuses via `ParsekLog` → `ScreenMessages` advisory.

---
