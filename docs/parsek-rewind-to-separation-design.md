# Parsek — Rewind to Separation

*Post-implementation design specification for the mid-mission rewind system that shipped in v0.9.0. Covers Rewind Points captured at multi-controllable split events (staging, undocking, EVA), the Unfinished Flights virtual group, the append-only supersede relation, narrow kerbal-death tombstone scope, the journaled staged commit, and the load-time sweep that keeps half-finished state bounded.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to an immutable timeline, and see previously recorded missions play back as ghost vessels alongside new ones. This document extends the flight recorder, timeline, and ledger systems with Rewind-to-Separation. It assumes familiarity with the recording DAG, BranchPoint model, controller identity, ghost chains, and the additive-only invariant (see `parsek-flight-recorder-design.md`) and with the ledger model, immutable ActionId, reservations, and career-state replay (see `parsek-game-actions-and-resources-recorder-design.md`).*

**Status:** shipped in v0.9.0.
**Pre-implementation spec:** `docs/dev/done/parsek-rewind-separation-design.md` (archived as-is alongside the v0.9 rollout). This document supersedes it as the source of truth for what actually shipped.
**Related docs:** `parsek-flight-recorder-design.md`, `parsek-recording-finalization-design.md`, `parsek-timeline-design.md`, `parsek-game-actions-and-resources-recorder-design.md`, `parsek-logistics-supply-routes-design.md`.

---

## 1. Introduction

A Parsek career is a committed timeline of missions. The player flies, commits, the ghost plays back, and the next mission begins on top. Before v0.9 the timeline had a sharp edge: once a multi-controllable split happened — a stage decoupled with a probe core on each side, a lander undocked from a station, a kerbal popped out on EVA — whichever half the player did not personally fly was recorded in the background and, if things went badly, committed as a crashed or destroyed sibling. There was no in-game path back to re-fly that half. The successful side was locked in with the failed side, and the only "fix" was to discard the entire tree and re-fly the whole mission.

Rewind to Separation is the narrow feature that unsticks that edge. At every split that produces two or more controllable entities, Parsek writes a transient KSP quicksave — a **Rewind Point** — plus a compact persistent-id-to-slot table captured at save time. If any sibling ends in **destruction or loss** — a crashed booster, a destroyed lander, a kerbal who fell without a parachute — the recording appears in a read-only **Unfinished Flights** group in the Recordings Manager. Clicking Rewind on an Unfinished-Flight row, either in that virtual group or on the same recording's normal table row, reloads the quicksave, strips the non-selected siblings (they play back as ghosts from their committed recordings), and hands the player the other half at the exact split moment. When the re-fly ends and the player merges, the new recording supersedes the old one via an **append-only relation** — the original recording is never mutated or deleted, the ghost/claim subsystems just filter it out.

**Stable-end splits are explicitly not in scope.** An orbital EVA where the kerbal boards back safely, a station docking, a stage separation where both halves continue to orbit intact — none of these need a Rewind button. The sibling reached a stable terminal state (`Orbiting`, `Landed`, `Splashed`, `Docked`, `Boarded`); there is nothing to "re-fly" because nothing went wrong. `EffectiveState.IsUnfinishedFlight` gates strictly on `TerminalKind.Crashed` for exactly this reason (see §5 and §7.31).

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
- **Load-time sweep.** `LoadTimeSweep.Run` (`Source/Parsek/LoadTimeSweep.cs:51`) validates the re-fly marker's six durable fields via `MarkerValidator.Validate` (`Source/Parsek/MarkerValidator.cs:86`), discards zombie NotCommitted provisionals and session-scoped RPs not referenced by a valid marker, warn-logs orphan supersede/tombstone rows, and clears stray `SupersedeTargetId` fields. Normal staging RPs with `CreatingSessionId == null` are retained across merge-dialog scene loads.
- **Revert-during-re-fly dialog.** `RevertInterceptor` (`Source/Parsek/RevertInterceptor.cs`) prefixes `FlightDriver.RevertToLaunch` via Harmony and routes the player to `ReFlyRevertDialog` (`Source/Parsek/ReFlyRevertDialog.cs`) with three options: Retry from Rewind Point, Discard Re-fly, Continue Flying.

### 1.2 Out of scope

v1 deliberately does NOT attempt:

- Re-emitting retired contract completions, milestone flags, tech research, facility upgrades, strategies, or the KSP subsystems that own them (`ContractSystem`, `ProgressTracking`, `ScenarioUpgradeableFacilities`, `StrategySystem`, `ResearchAndDevelopment`). These are "sticky" — KSP will not re-emit a contract completion after `ContractSystem` marks it complete, so tombstoning the event and hoping the re-fly emits a fresh one produces zero credit. v1 sidesteps this by leaving all non-kerbal-death ledger actions in the Effective Ledger Set regardless of whether their source recording was superseded. See §2, §3, §8, §10 for the exact framing.
- Nested re-fly sessions. The precondition gate rejects a rewind invocation while another session is active (`RewindInvoker.cs:108`).
- Cross-tree supersedes. v1 does not produce them; `EffectiveState.EffectiveRecordingId` does not yet halt at cross-tree boundaries but has a TODO marker for when cross-tree supersedes become producible (see Known Limitations).
- Auto-purge policies for long-lived reap-eligible RPs. Monitoring via the disk-usage diagnostic is the v1 answer.
- A recovery-snapshot mechanism for corrupt quicksaves. The pre-impl `SplitTimeSnapshot` concept was dropped before v1 shipped; the KSP quicksave is the sole source of truth.

### 1.3 Who benefits

- Players who launch rockets with recoverable boosters or stages and whose booster crashed instead of landing — they want to re-fly the booster back to the pad while the upper stage's ascent to orbit continues to play back as a ghost.
- Players whose kerbal died during an EVA that turned out to be avoidable (e.g. a fall without a parachute) — they want to replay the EVA so the kerbal survives, while the vessel the kerbal came from retains whatever stable state it actually reached (orbit, dock).
- Players who lost a lander on descent while the orbiting mothership survived — they want to re-fly the lander.

What this feature is **not** for: stable orbital splits, safe returns, or routine dockings. If the sibling reached a stable terminal state (orbiting, landed without breaking, docked, boarded), the recording spawns its end state on playback and the mission is done; no Rewind button appears.

### 1.4 Relationship to prior features

Rewind to Separation is layered on top of, not inside, the existing subsystems:

- **Flight recorder:** the recording DAG, segment boundary rule, controller identity, ghost chains, background recording, terminal kinds. Rewind to Separation does not change any of these. It adds new persistent state — Rewind Points, supersede relations, tombstones, a session marker, a journal — stored alongside the existing recording tree in `ParsekScenario`.
- **Recording finalization:** Rewind-to-Separation assumes each sibling recording has a trustworthy terminal state and endpoint. The finalization reliability contract in `parsek-recording-finalization-design.md` is the upstream dependency that prevents Unfinished Flights from depending on stale last-sample inference when KSP unloads, deletes, or destroys a vessel before scene exit.
- **Timeline / ledger:** the immutable `ActionId`, the recalculation engine, the resource modules. The feature adds `GameAction.ActionId` as a hard precondition (legacy migration generates a deterministic hash on first load) and introduces `LedgerTombstone` as an append-only retirement filter, but the recalculation walk itself is unchanged — `LedgerOrchestrator.Recalculate*` now feeds from `EffectiveState.ComputeELS()` (the tombstone-filtered view) instead of raw `Ledger.Actions`.
- **Game actions & resources:** contracts, milestones, facilities, strategies, tech, science, funds, kerbals. v1 tombstones only kerbal deaths (plus bundled rep). Everything else sticks. This is the central design decision; see §2 and §10.

---

## 2. Design Philosophy

These principles governed every design and implementation decision. They are listed up front because they inform every section that follows.

### 2.1 Correct visually, minimal, efficient

Borrowed from the project-wide recording-design principle. A Rewind Point is 1–2 MB of quicksave. Many splits do not end in regret. Therefore: RP creation is cheap, deferred, and speculative; reap is eager (as soon as no slot can be re-flown, the file is deleted); cached ERS/ELS views rebuild only when source state changes; supersede filtering is a derived lookup, not a stored field.

### 2.2 Append-only history

The recording tree never shrinks. Supersede is a relation stored in a separate list, not a mutation on a recording. Tombstones are append-only. No field on a committed Recording is written after its `MergeState` flips out of `NotCommitted`. `EffectiveState.IsVisible` (`Source/Parsek/EffectiveState.cs:129`) is computed by walking `RecordingSupersedeRelation` entries; the Recording itself carries no `SupersededByRecordingId` field. Tree-scoped rewind-state purge (triggered by merge-dialog Discard or any other whole-tree discard path, §6.17) is the only path that ever removes supersede relations or tombstones, and it clears all such rewind artifacts for the tree atomically. The Revert-during-re-fly dialog's Discard Re-fly option is NOT a tree purge — it is session-scoped (§6.14) and preserves the tree's supersede / tombstone / RP state. The tree's committed recordings themselves are never rewritten — separate pending-tree discard handles removal of pre-commit recordings, and committed recordings stay in the timeline indefinitely.

### 2.3 Narrow v1 semantics

The central design decision. Supersede is a **physical-visibility / claim-tracker mechanism**, not a ledger or career-state eraser. The v1 tombstone-eligible type list is deliberately small: `KerbalAssignment`-to-Dead and the `ReputationPenalty` actions bundled with them. Everything else — contracts, milestones, facilities, strategies, tech, science, funds, vessel-destruction rep — remains in the Effective Ledger Set after supersede. This produces behavior the player can reason about: "my career state from the failed attempt stays; only the kerbals come back." It also produces behavior the mod can reliably deliver, because re-emitting retired KSP events is not generally possible.

### 2.4 Crash-recoverable merge

Every staged-commit merge step is journaled through `MergeJournal.Phase` (`Source/Parsek/MergeJournal.cs:53`). Nine phase strings mark the merge's progress; on load, the finisher reads the phase and either rolls back (pre-Durable-1 phases: disk still holds the pre-merge snapshot, in-memory changes evaporate) or drives to completion (post-Durable-1 phases: disk holds the merged state, the remaining steps resume). The journal IS the durability barrier; the next natural `ScenarioModule.OnSave` flushes each phase's in-memory state to disk. Tests inject a synchronous save stub to exercise every window (see `MergeCrashRecoveryMatrixTests.cs`).

### 2.5 Atomic provisional + marker write

The provisional re-fly Recording and its `ReFlySessionMarker` must land in the scenario in the same synchronous block; no KSP `OnSave` may capture the first without the second. `RewindInvoker.AtomicMarkerWrite` (`RewindInvoker.cs:463`) enforces this: the provisional is added, the marker is constructed and assigned, and a `try/catch` rolls back both if anything throws. There is no coroutine yield, no deferred save, and no frame gap between `CheckpointA:BeforeProvisional` and `CheckpointB:AfterMarker` — the `CheckpointHookForTesting` observer (`RewindInvoker.cs:47`) is the only thing that can see in between.

### 2.6 Player-visible opt-in

A re-fly never happens behind the player's back. It requires an explicit click on the Rewind button for an Unfinished Flight row; both the virtual group copy and the normal recordings-table copy route through `RewindInvoker` instead of the legacy tree-root launch rewind. The confirmation dialog names the canonical semantics (`RewindInvoker.ShowDialog` body at `RewindInvoker.cs:157`). Rewind Points are written automatically on every multi-controllable split, but they are invisible until a sibling ends up Unfinished — the player sees the UI entry only when it is actionable.

### 2.7 Observable from logs alone

Every decision point in §6 and every RP / marker / journal / sweep / reap state transition emits a log line. The tag catalog in §11 is what a KSP.log reader needs to reconstruct a session's history: the `[Rewind]`, `[RewindSave]`, `[RewindUI]`, `[ReFlySession]`, `[Supersede]`, `[LedgerSwap]`, `[MergeJournal]`, `[LoadSweep]`, `[UnfinishedFlights]`, `[CrewReservations]`, `[RevertInterceptor]`, `[Recording]`, `[ReconciliationBundle]`, `[ERS]`, `[ELS]` tags appear exactly where §6 and §11 say they do.

---

## 3. Terminology

This section fixes the vocabulary. Throughout the doc, "recording" (lowercase) is the general concept; **Recording** (capitalized, code-font) is the class (`Source/Parsek/Recording.cs`). "Session" always means a Rewind-to-Separation re-fly session unless qualified.

- **Split event** — a `BranchPoint` whose type is `JointBreak`, `Undock`, or `EVA` (see `parsek-flight-recorder-design.md`). The common case is staging (joint break on the decoupler), but lander undocks and EVAs are the same shape.
- **Controllable entity** — a vessel with at least one `ModuleCommand` part, or an EVA kerbal. The classifier `SegmentBoundaryLogic.IsMultiControllableSplit` gates RP creation on `count >= 2`.
- **Multi-controllable split** — a split event that produces two or more controllable entities. Only these get a Rewind Point.
- **Rewind Point (RP)** — the durable object at a multi-controllable split. Holds the quicksave filename, the save UT, the ChildSlots, the PidSlotMap, the RootPartPidMap, plus bookkeeping fields. Defined in `Source/Parsek/RewindPoint.cs`. "Session-provisional" during re-fly; "persistent" after session merge.
- **Child slot** — a stable, ordered entry on an RP identifying one controllable output of the split. Carries `OriginChildRecordingId` (immutable) and derives `EffectiveRecordingId` via a forward walk through `RecordingSupersedeRelation`. Defined in `Source/Parsek/ChildSlot.cs`.
- **PidSlotMap** — `Dictionary<uint, int>` mapping `Vessel.persistentId` to `ChildSlot.SlotIndex`. Populated at quicksave-write time by `RewindPointAuthor.ExecuteDeferredBody` (`Source/Parsek/RewindPointAuthor.cs:202`). The authoritative primary match for the post-load strip.
- **RootPartPidMap** — `Dictionary<uint, int>` mapping root-part `Part.persistentId` to `ChildSlot.SlotIndex`. Populated at the same time as PidSlotMap. Fallback match when KSP has reassigned a vessel-level persistentId between save and load. `Part.persistentId` is the stable cross-save/load identity for a physical part; `Part.flightID` is session-scoped and unstable.
- **Finished recording** — a Recording with `MergeState == Immutable`.
- **Unfinished Flight** — a Recording matching `EffectiveState.IsUnfinishedFlight` (see §5). Visible in the ERS, terminal state classified as Crashed, parent BranchPoint carries a non-null `RewindPointId`, and a live RewindPoint entry exists for that BranchPoint.
- **Re-fly session** — the period between a Rewind Point invocation and its merge / discard. Identified by a `SessionId` GUID generated at invocation (`RewindInvoker.cs:230`). Ends on merge, discard, retry (new SessionId), full-revert, or load-time validation failure.
- **Session-provisional RP** — an RP whose `SessionProvisional` flag is `true`. Newly created RPs are session-provisional; the flag flips to `false` during the merge's `TagRpsForReap` step (`MergeJournalOrchestrator.cs:441`) for RPs whose `CreatingSessionId` matches the merging session. Load-time sweep discards session-provisional RPs only when they are session-scoped (`CreatingSessionId` non-empty) and not referenced by a valid marker; normal staging RPs created outside a re-fly session have `CreatingSessionId == null`, survive merge-dialog scene loads, and are promoted to persistent when the owning tree commits.
- **Supersede** — an append-only relation `RecordingSupersedeRelation { RelationId, OldRecordingId, NewRecordingId, UT, CreatedRealTime }` stored in `ParsekScenario.RecordingSupersedes`. No Recording is mutated; `EffectiveState.IsVisible` filters via the list.
- **Supersede chain** — the forward walk through `RecordingSupersedes` from a `ChildSlot.OriginChildRecordingId`. Chains can extend through multiple re-fly attempts (AB → AB' → AB'' if AB' was merged as a crashed `CommittedProvisional`).
- **Session-suppressed subtree** — during an active re-fly, a second narrow carve-out applied to physical-visibility subsystems only (ghost walker, chain tip, map presence, watch mode). Forward-only closure from the marker's `OriginChildRecordingId`, halting at mixed-parent BranchPoints. Each member also expands to its chain siblings (same `ChainId` AND same `ChainBranch`) so a `RecordingOptimizer.SplitAtSection` env split between HEAD and TIP is suppressed atomically — without this, the merge-time supersede would only retire the BP-linked HEAD and leave the terminal-Destroyed TIP visible. Different `ChainBranch` values stay independent (parallel ghost-only continuations). Computed by `EffectiveState.ComputeSessionSuppressedSubtree` (called from `Source/Parsek/SessionSuppressionState.cs`). Career and ledger subsystems do NOT consume this view — they read ERS / ELS directly.
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

**Chain-sibling expansion.** Each member added to the closure also expands to its chain siblings — committed recordings sharing both `ChainId` and `ChainBranch`. Merge-time `RecordingOptimizer.SplitAtSection` splits a single live recording at env boundaries (atmo↔exo) into a `ChainId`-linked HEAD + TIP where the HEAD keeps the parent-branch-point link and the TIP carries the terminal state. Without this expansion, walking by `ChildBranchPointId` alone would dequeue the HEAD, find no BP descendant (its `ChildBranchPointId` was moved to the TIP at split time), and stop — leaving the terminal-Destroyed TIP visible alongside the new "kerbal lived" provisional after merge. Different `ChainBranch` values stay independent (parallel ghost-only continuations are not auto-suppressed together). Same dual-key contract as `EffectiveState.IsChainMemberOfUnfinishedFlight` and `ResolveChainTerminalRecording`.

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
| `CreatingSessionId` | `string` | Session GUID for recordings produced during an active re-fly. `null` outside sessions. Used by the load-time spare-set logic (§6.16) when a session crashed. |
| `SupersedeTargetId` | `string` | Transient — set only on `NotCommitted` provisional re-fly recordings to signal the intended supersede target. Cleared at merge when a concrete `RecordingSupersedeRelation` is appended. A non-empty value on `Immutable` / `CommittedProvisional` recordings triggers a Warn log and is treated as cleared (load-path legacy safety; see §6.16 step 5 and `LoadTimeSweep.ClearStraySupersedeTargets`). |
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
| `SessionProvisional` | `bool` | `true` while the creating session still owns the RP. Normal split RPs created outside a re-fly session remain `true` with `CreatingSessionId == null` only until their owning tree is accepted; load-time sweep retains them during merge-dialog scene loads, and the tree-commit path promotes them to persistent. Session-scoped RPs flip `false` at `TagRpsForReap` during merge. |
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

Stored in `ParsekScenario.RecordingSupersedes`. Append-only during normal play. Removed by whole-tree discard (`TreeDiscardPurge.PurgeTree`, §6.17), and by load-time cleanup when a row is fully orphaned (both endpoint ids are missing from `RecordingStore`). One-sided orphan relations stay in place with a Warn log on every load (`LoadTimeSweep.cs`) because an old-present/new-missing row still suppresses the retired old recording; the forward walk in `EffectiveState.EffectiveRecordingId` handles an unresolved `NewRecordingId` as a chain terminator.

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

Not stored in `GroupHierarchyStore`. Membership derived per frame from ERS filtered by `EffectiveState.IsUnfinishedFlight`. As a system group it **cannot be hidden** and **cannot be a drop target** for manual group assignment. Rename on an individual member row still persists through the standard `Recording.VesselName` path; hide on a member row warns and refuses via `ParsekLog` → `ScreenMessages` advisory.

The virtual group is not the only safe affordance. The same recording can also appear in the normal recordings table, where its tree root still carries a legacy launch rewind save. RP-backed unfinished-flight rows therefore preempt the normal `RecordingStore.CanRewind` / `InitiateRewind` path and resolve the row's child slot before any legacy fallback is considered.

---

## 6. Behavior

### 6.1 Effective Recording Set (ERS)

File: `Source/Parsek/EffectiveState.cs:236`.

```
ERS = { r in RecordingStore.CommittedRecordings
      : r.MergeState != NotCommitted
        AND IsVisible(r, ParsekScenario.RecordingSupersedes)
        AND (activeMarker == null OR r.RecordingId not in SessionSuppressedSubtree(activeMarker)) }

IsVisible(r, supersedes) :=
    walk forward from r.RecordingId via supersedes,
    return true iff the walk terminates at r.RecordingId itself.
```

Cached. Rebuild triggered by any change to `RecordingStore.StateVersion`, `ParsekScenario.SupersedeStateVersion`, or the active `ReFlySessionMarker` identity. Every rebuild emits a single `[ERS]` Verbose line:

```
[Parsek][Verbose][ERS] Rebuilt: <N> entries from <raw> committed
(skippedNotCommitted=<k> skippedSuperseded=<s> skippedSuppressed=<u>
marker=<sessionId|none>)
```

### 6.2 Effective Ledger Set (ELS)

File: `Source/Parsek/EffectiveState.cs:310`.

```
ELS = { a in Ledger.Actions : a.ActionId not in { t.ActionId for t in ParsekScenario.LedgerTombstones } }
```

**One filter only: tombstones.** There is no recording-level filter. A superseded recording's non-eligible ledger actions (contracts, milestones, facilities, strategies, tech, science, funds, non-kerbal-death rep) remain in ELS. The only way an action exits ELS is via an explicit `LedgerTombstone` carrying its `ActionId`.

Cached. Rebuild triggered by any change to `Ledger.StateVersion` or `ParsekScenario.TombstoneStateVersion`. Every rebuild emits a single `[ELS]` Verbose line with the count and skipped-tombstoned counter.

### 6.3 Session-suppressed subtree (physical-visibility only)

Computed by `EffectiveState.ComputeSessionSuppressedSubtree(marker)`. Forward-only closure from `marker.OriginChildRecordingId`:

```
closure(r) = { r }
  for each child c whose ParentRecordingIds contain r:
    if c.ParentRecordingIds is a subset of the current closure:
      include c (and recurse)
    else:
      stop — c has a parent outside the subtree (mixed-parent halt)
```

Consumed by physical-visibility subsystems:

- **Ghost playback engine** (`GhostPlaybackEngine.UpdatePlayback`) — per-frame filter with a `sessionSuppressed=<n>` counter in the frame summary log; destroys leftover ghost visuals on session entry.
- **Ghost chain walker** (`GhostChainWalker`) — suppressed recordings do not claim chain tips. Aggregated skip log.
- **Ghost map presence** (`GhostMapPresence`) — create-entry-points gate + per-tick prune of suppressed presence.
- **Watch mode** (`WatchModeController`) — refuses entry on suppressed anchor; exits on session-start if the current anchor is suppressed.

Subsystems that read ERS / ELS directly and do NOT apply the session-suppressed filter:

- Ledger recalculation (`LedgerOrchestrator`) reads ELS directly via `EffectiveState.ComputeELS()`.
- Career-state UI (Contracts / Strategies / Facilities / Milestones tabs).
- Crew reservation manager (`CrewReservationManager`), with a narrow live-re-fly-crew carve-out (see §6.4).
- Contract / milestone / facility state derivation (KSP-owned; Parsek never touches on supersede).
- Timeline view.
- Unfinished Flights membership.
- RewindPoint reap logic.

Exactly one `[ReFlySession] Start` / `End` log line is emitted per transition via `SessionSuppressionState` (`Source/Parsek/SessionSuppressionState.cs`), which observes marker-identity changes and logs once per transition.

### 6.4 Kerbal dual-residence carve-out (during active session)

`CrewReservationManager.IsLiveReFlyCrew(pcm, marker)` exempts kerbals physically embodied in the live re-fly vessel from reservation lock. Without this carve-out, a kerbal whose ledger `KerbalAssignment`-to-Dead event is still in ELS (tombstone not yet written — that happens at merge) would be reservation-locked as dead, silently blocking EVA or crew transfer during the re-fly.

Other reservation effects (kerbals reserved to unrelated still-ghost recordings, stand-in generation for other unreserved slots) remain active.

### 6.5 Multi-controllable split detection

Every joint-break, undock, and EVA event funnels through `SegmentBoundaryLogic.ClassifyJointBreakResult` and its siblings. `SegmentBoundaryLogic.IdentifyControllableChildren` (`SegmentBoundaryLogic.cs:235`) enumerates the post-split vessels and counts the ones that have a `ModuleCommand` part or are EVA kerbals. `SegmentBoundaryLogic.IsMultiControllableSplit(count)` is a one-line gate: `count >= 2`.

When the gate passes, `ParsekFlight.CreateSplitBranch` / `ProcessBreakupEvent` / the EVA handler invokes `RewindPointAuthor.Begin` with the new `BranchPoint`, the list of `ChildSlot`s (one per controllable child), and the list of controllable child PIDs. For BG-recorded splits that originate outside the focus vessel, `BackgroundRecorder` takes the same path with a cached PID payload.

### 6.6 Rewind Point capture

File: `Source/Parsek/RewindPointAuthor.cs`.

`Begin` runs synchronously (same frame as the split):

1. **Scene guard**: if `HighLogic.LoadedScene != GameScenes.FLIGHT`, abort with `[RewindSave] Warn: Aborted: scene=<loaded>`. Return null.
2. **Scenario guard**: verify `ParsekScenario.Instance` is live (using `ReferenceEquals` to bypass Unity's overloaded `== null` for test fixtures).
3. Generate `rpId = "rp_" + Guid.NewGuid().ToString("N")` and validate via `RecordingPaths.ValidateRecordingId`.
4. Build the RP stub with empty `PidSlotMap` / `RootPartPidMap`, `SessionProvisional = true`, `CreatingSessionId` set from the active marker if one is live.
5. Append the RP to `ParsekScenario.RewindPoints` and stamp `BranchPoint.RewindPointId = rpId`. This is the synchronous wiring — an `OnSave` triggered between `Begin` and the deferred coroutine will already see the in-progress RP.
6. Emit `[Rewind] Info: RewindPoint begin: rp=<id> bp=<bpId> slots=<N> controllablePids=<M> ut=<x>`.
7. Start the deferred coroutine (or, in tests, the `SyncRunForTesting` hook).

`RunDeferred` / `ExecuteDeferredBody` (one-frame-deferred):

1. Re-check scene guard; if we left FLIGHT between `Begin` and the deferred frame, `RollbackBegin` removes the RP from the scenario and clears the BranchPoint back-reference.
2. Drop high warp if `CurrentRateIndex > 1`. Wrapped in a `finally` so a mid-save throw still restores the player's rate.
3. Populate `PidSlotMap` and `RootPartPidMap` by iterating ChildSlots. For each slot, resolve the expected `Vessel.persistentId` via the injected `RecordingResolver` (or `CommittedRecordings` fallback), look up the live vessel via `FlightGlobalsProvider.TryGetVesselSnapshot`, and record `{pid → slotIdx}` and `{rootPid → slotIdx}`. A slot with no live vessel is marked `Disabled = true` with `DisabledReason = "no-live-vessel"`.
4. Log `[Rewind] Info: RewindPoint slot capture: rp=<id> populated=<p>/<total> disabled=<d>`.
5. If every slot failed, mark the RP `Corrupted = true` but continue with the save so the on-disk quicksave is still available for diagnostics.
6. Call `GamePersistence.SaveGame(tempName, saveFolder, SaveMode.OVERWRITE)` to `Parsek_TempRP_<rpId>.sfs` in the save root. KSP always writes to the save root per its own convention.
7. `FileIOUtils.SafeMove` the temp file to `<saveDir>/Parsek/RewindPoints/<rpId>.sfs` (atomic tmp + rename). `RecordingPaths.EnsureRewindPointsDirectory` creates the directory on demand.
8. Log `[RewindSave] Info: Wrote rp=<id> path=<relPath> bytes=<N> ms=<t>`. On any failure in steps 6-7, call `RollbackBegin` so a partial-write never leaves a half-registered RP.
9. Restore warp rate in the `finally`; log `[RewindSave] Info: Warp restored to <rate> for rp=<id>` or `[RewindSave] Warn: Warp restore to <rate> failed for rp=<id>: <reason>`.

Failure modes: disk full, permissions, save exception, scene transition between `Begin` and deferred frame. All paths either roll back cleanly or keep a Corrupted RP for diagnostics; the split itself proceeds normally either way.

### 6.7 Between split and merge

The recorder keeps recording normally: the focused vessel is actively sampled, unfocused controllable siblings are BG-recorded. All have `MergeState = NotCommitted`.

On session merge (the NON-re-fly merge that commits the tree that just ran):

- Focused vessel's recording → `Immutable`.
- BG-recorded controllable children whose parent BranchPoint has `RewindPointId != null` → `CommittedProvisional`. (They might become Unfinished Flights.)
- Other BG children under a BranchPoint without an RP → `Immutable`.
- Session-provisional RPs promote to persistent during `TagRpsForReap` when their `CreatingSessionId` matches the merging session — but for tree merges outside an active re-fly session, `CreatingSessionId` is null on the RPs written during flight, so the session promote path doesn't apply. `LoadTimeSweep` retains those normal staging RPs during the scene load that presents the merge dialog; `RecordingStore.CommitTree` promotes the accepted tree's normal staging RPs to persistent. Nested/session-created RPs with a dead `CreatingSessionId` are the ones purged. Unfinished Flights membership recomputes.

Note: the distinction between "session merge of a normal flight" and "session merge of a re-fly session" is driven by whether `ActiveReFlySessionMarker` is non-null. Only re-fly merges go through `MergeJournalOrchestrator.RunMerge`.

### 6.8 Invoking a Rewind Point

File: `Source/Parsek/RewindInvoker.cs`.

**Precondition gate** (`CanInvoke`, `RewindInvoker.cs:63`). Five checks, each emitting a reason string on failure:

1. Scene is `FLIGHT` / `SPACECENTER` / `TRACKSTATION` (not a transition).
2. `RewindInvokeContext.Pending` is false (no in-flight invocation).
3. RP is not `Corrupted`.
4. Quicksave file exists on disk at the expected path (`ResolveAbsoluteQuicksavePath`).
5. No active re-fly session (`ParsekScenario.Instance.ActiveReFlySessionMarker == null`).
6. Deep-parse precondition passes: the quicksave is loaded as a ConfigNode and every `PART` node's `name` must resolve via `PartLoader.getPartInfoByName`. Cached per RP for 60 seconds via `PreconditionCache`. Missing parts mark the RP `Corrupted`.

**Confirmation dialog** (`ShowDialog`, `RewindInvoker.cs:138`). A `PopupDialog.SpawnPopupDialog` with a MultiOptionDialog. The message names the UT, the selected slot / origin recording, and the narrow supersede semantics:

```
Rewind to rewind point <rpId> at UT <utText>?
Spawning the selected child (slot <N>, origin=<rec>) live; merged siblings will play as ghosts.

Career state during this attempt stays as it is now. Supersede on merge
retires only kerbal-death events; contract / milestone / facility / strategy
/ tech / science / funds state is unchanged.
```

Buttons: `Rewind` (fires `StartInvoke`), `Cancel` (logs and dismisses).

**Pre-load phase** (`StartInvoke`, `RewindInvoker.cs:206`). All synchronous, no yield:

1. Generate `sessionId = "sess_" + Guid.NewGuid().ToString("N")`.
2. `ReconciliationBundle.Capture()` snapshots every in-memory state that must survive the KSP scene reload: committed recordings, committed trees, ledger actions, ledger tombstones, scenario lists (RPs, supersedes, marker, journal), crew reservations, group hierarchy, milestone store, ResourcesApplied flags. Any exception aborts with `[Rewind] Error` and a user toast.
3. `CopyQuicksaveToSaveRoot` copies `saves/<save>/Parsek/RewindPoints/<rpId>.sfs` to `saves/<save>/Parsek_Rewind_<sessionId>.sfs` (root). Required because `GamePersistence.LoadGame` does not support subdirectory paths.
4. Store the session id, bundle, RP, selected slot, temp path, and slot metadata in the static `RewindInvokeContext`. This is the only state that survives the scene reload.
5. `HighLogic.CurrentGame = GamePersistence.LoadGame(tempLoadName, HighLogic.SaveFolder, true, false)` + `HighLogic.LoadScene(GameScenes.FLIGHT)`. From here, Unity tears down the current scenario; only the static context survives.

Any failure between steps 2 and 5 triggers `TryRestoreBundle` + `HandleQuicksaveMissing` (clears filename + marks Corrupted if the file really is missing) + toast + context clear.

**Post-load phase** (`ConsumePostLoad`, `RewindInvoker.cs:335`). Called by `ParsekScenario.OnLoad` exactly once per invocation. All synchronous:

1. `ReconciliationBundle.Restore(bundle)` re-applies the pre-load in-memory state over the quicksave-loaded state. The quicksave's `RecordingStore`, `Ledger.Actions`, scenario lists, crew reservations, etc. are discarded; the bundle's are restored. `Planetarium.UT` remains at the quicksave's UT (that's the whole point).
2. `PostLoadStripper.Strip(rp, selectedSlotIndex)` strips the non-selected siblings (§6.9).
3. `FlightGlobals.SetActiveVessel(selectedVessel)`.
4. `AtomicMarkerWrite(rp, selected, stripResult, sessionId)` — the critical section (§6.10).
5. `LedgerOrchestrator.RecalculateAndPatch()` — non-fatal; a throw logs Warn but does not fail the invocation.
6. `finally`: delete the root-level temp quicksave and clear `RewindInvokeContext`.

### 6.9 Post-load strip (authoritative matching)

File: `Source/Parsek/PostLoadStripper.cs`.

For each live `Vessel` in the enumeration:

1. **Ghost ProtoVessel guard**: if `GhostMapPresence.IsGhostMapVessel(pid)`, skip. Do not strip — it is a Parsek-spawned map presence. Increment `GhostsGuarded`.
2. **Primary match** via `RewindPoint.PidSlotMap`: if `pid` is in the map, record the match with the slot index.
3. **Fallback match** via `RewindPoint.RootPartPidMap` using the vessel's root-part `Part.persistentId`: if primary miss but rootPart is present and the map has a matching entry, record the match and increment `FallbackMatches`; log `[Rewind] Warn: Fallback match via root-part v=<pid> rootPart=<rootPid> slotIdx=<idx>`.
4. **No match**: `LeftAlone`++. Log `[Rewind] Verbose: Strip leaveAlone: unrelated v=<pid> name=<name>`.

After enumeration: walk the matches. The vessel matching the selected slot is kept as `SelectedVessel`; all other matches are stripped via `Vessel.Die()`. If multiple vessels match the selected slot (shouldn't happen in practice), the first is kept and the rest are stripped with a `[Rewind] Warn: Multiple vessels match selectedSlot=<n>`.

Final log: `[Rewind] Info: Strip stripped=[<indices>] selected=<idx> ghostsGuarded=<N> leftAlone=<M> fallbackMatches=<f>`.

If the stripper returns `SelectedPid == 0u` (selected vessel not present on reload), `ConsumePostLoad` toasts an error and aborts — the quicksave is stale relative to the RP's expectations.

### 6.10 Atomic provisional + marker write

File: `RewindInvoker.cs:463` (`AtomicMarkerWrite`).

The critical section. Runs synchronously; throws MUST leave global state untouched. NO yield, NO await, NO deferred save.

1. `CheckpointHookForTesting("CheckpointA:BeforeProvisional")`.
2. Build the provisional Recording (`BuildProvisionalRecording`, `RewindInvoker.cs:527`): fresh `RecordingId = "rec_" + Guid.NewGuid().ToString("N")`, `MergeState = NotCommitted`, `CreatingSessionId = sessionId`, `SupersedeTargetId = selected.OriginChildRecordingId`, `ProvisionalForRpId = rp.RewindPointId`, `ParentBranchPointId` cloned from the origin child (or the RP's BranchPoint as fallback), `TreeId` cloned from the origin child, `VesselPersistentId = stripResult.SelectedPid`, `VesselName = stripResult.SelectedVessel.vesselName`.
3. `RecordingStore.AddProvisional(provisional)`.
4. `CheckpointHookForTesting("CheckpointA:AfterProvisional")`.
5. Build the `ReFlySessionMarker` from the provisional, selected slot, and RP.
6. `CheckpointHookForTesting("CheckpointB:BeforeMarker")`.
7. `scenario.ActiveReFlySessionMarker = marker; scenario.BumpSupersedeStateVersion();`
8. `CheckpointHookForTesting("CheckpointB:AfterMarker")`.
9. Log `[ReFlySession] Info: Started sess=<sid> rp=<id> slot=<idx> provisional=<rid> origin=<oid> tree=<tid>`.

A `catch` around steps 5–8 rolls back both: removes the provisional via `RecordingStore.RemoveCommittedInternal(provisional)` and sets `ActiveReFlySessionMarker = null`. Both are idempotent. Rollback is the reason `try/catch` wraps those steps rather than wrapping the whole method — a mid-provisional throw doesn't leave a half-marker behind either.

KSP's `ScenarioModule.OnSave` cannot preempt this synchronous block. The `CheckpointHookForTesting` is the only observer that can see between checkpoints — tests use it to verify no save boundary is crossed.

### 6.11 Within a re-fly session

Ghost playback engine, chain walker, map presence, watch mode enumerate `ERS \ SessionSuppressedSubtree(marker)`. Career subsystems read ERS / ELS directly.

Nested multi-controllable splits during the session create additional session-provisional RPs with `CreatingSessionId = activeSession.SessionId`. Child recordings born in those splits carry the same `CreatingSessionId`. On session merge, all promote; on discard, all purge (load-time sweep spare-set logic, §6.16).

### 6.12 Merging a re-fly session (staged commit, journaled)

File: `Source/Parsek/MergeJournalOrchestrator.cs`.

Triggered by `MergeDialog.MergeCommit` when `ActiveReFlySessionMarker` is non-null. The 14-step design-spec merge is consolidated into nine phase transitions:

1. **Journal write, phase = `Begin`** (`MergeJournalOrchestrator.cs:189`). `scenario.ActiveMergeJournal` assigned. Log `[MergeJournal] Info: sess=<sid> phase=Begin`.
2. **Supersede relations** (`AdvancePhase → Supersede`). `SupersedeCommit.AppendRelations` computes the forward-only merge-guarded subtree closure via `EffectiveState.ComputeSessionSuppressedSubtree(marker)`, appends one `RecordingSupersedeRelation { old=subtreeId, new=provisional.Id, UT, CreatedRealTime }` per subtree id. Idempotent — a pre-existing relation with the same old/new pair is skipped with a Verbose log. Log `[Supersede] Info: Added <N> supersede relations for subtree rooted at <originId>`.
3. **Tombstones** (`AdvancePhase → Tombstone`). `SupersedeCommit.CommitTombstones` walks `Ledger.Actions`, groups in-scope actions by RecordingId for bounded rep-pairing, emits `LedgerTombstone`s for every action passing `TombstoneEligibility.IsEligible`. See §6.13. `TombstoneStateVersion` bumped. `CrewReservationManager.RecomputeAfterTombstones` re-derives reservations; death-tombstoned kerbals return to active. Log `[LedgerSwap] Info: Tombstoned <N> actions (KerbalDeath=<d>, repBundled=<r>); <M> actions matched subtree but type-ineligible (breakdown: contract=<c> milestone=<m> facility=<f> strategy=<s> tech=<t> science=<sc> funds=<fn> rep-unbundled=<ru>)` + `[Supersede] Info: Narrow v1: physical playback replaced; career state unchanged except kerbal deaths`.
4. **Finalize** (`AdvancePhase → Finalize`). `SupersedeCommit.FlipMergeStateAndClearTransient` flips `provisional.MergeState` to `Immutable` (Landed / InFlight) or `CommittedProvisional` (Crashed), clears `SupersedeTargetId`, bumps `SupersedeStateVersion`. Marker preserved (`preserveMarker: true`) so it stays live across Durable Save #1.
5. **Durable Save #1** (`AdvancePhase → Durable1Done`). Deferred to the next natural `ScenarioModule.OnSave` in production; tests inject a synchronous hook. This is the durability barrier — on-disk state is now supersedes + tombstones + MergeState all committed; marker + RPs still live.
6. **RP reap** (`AdvancePhase → RpReap`). `TagRpsForReap(marker, scenario)` flips `SessionProvisional = false` on every RP with matching `CreatingSessionId`, and defensively promotes the marker's origin RP when it is a normal staging RP with `CreatingSessionId == null`. Then `RewindPointReaper.ReapOrphanedRPs` walks all RPs; for each non-session-provisional RP, it walks every ChildSlot's `EffectiveRecordingId` and marks the RP reap-eligible if every effective recording has `MergeState == Immutable`. Eligible RPs get their quicksave file deleted (`File.Delete` is best-effort), scenario entry removed, BranchPoint back-reference cleared. Log `[Rewind] Info: ReapOrphanedRPs: reaped=<R> remaining=<rem>`.
7. **Marker clear** (`AdvancePhase → MarkerCleared`). `scenario.ActiveReFlySessionMarker = null`; `BumpSupersedeStateVersion`. Log `[ReFlySession] Info: End reason=merged sess=<sid> provisional=<rid>`.
8. **Durable Save #2** (`AdvancePhase → Durable2Done`).
9. **Clear journal** (`ClearJournalAndFinalSave`). `journal.Phase = Complete`; Durable Save #3; `ActiveMergeJournal = null`. Log `[MergeJournal] Info: Completed sess=<sid>`.

**Failure recovery matrix** (`RunFinisher`, `MergeJournalOrchestrator.cs:263`). On load, if `ActiveMergeJournal != null`:

| Phase on disk | Recovery |
|---|---|
| `Begin`, `Supersede`, `Tombstone`, `Finalize` (pre-Durable-1) | **Rollback.** The first durable save never happened; disk still holds the pre-merge snapshot. `RollBack` removes the in-memory session-provisional recording (idempotent when already absent), clears marker, clears journal, Durable Save. `[MergeJournal] Info: Rolled back from phase=<p>: session restored sess=<sid> markerCleared=<b> provisionalRemoved=<r>`. |
| `Durable1Done`, `RpReap`, `MarkerCleared`, `Durable2Done`, `Complete` (post-Durable-1) | **Complete.** `CompleteFromPostDurable` drives the remaining steps. Idempotent: missing file deletions are no-ops, already-cleared marker is a no-op. `[MergeJournal] Info: Completed from phase=<p> sess=<sid> stepsDriven=<n>`. |

The finisher is triggered by journal presence, **not** by marker state. This is the v0.5 pre-impl correction that survived into v1.

### 6.13 v1 tombstone-eligible scope

File: `Source/Parsek/GameActions/TombstoneEligibility.cs` + `TombstoneAttributionHelper.cs`.

An action `a` is in supersede scope when:
- `a.RecordingId` is non-null AND `a.RecordingId` is in the forward-only merge-guarded subtree closure; OR
- (design-spec allowance for null-scoped attribution; v1 does NOT tombstone null-scoped actions per §7.41).

For actions in scope, eligibility is narrow:

| `GameAction.Type` | Eligible? | Condition |
|---|---|---|
| `KerbalAssignment` (with `Dead = true`) | yes | Direct eligibility. |
| `ReputationPenalty` | yes | Paired with a same-recording kerbal-death action within a 1s UT window. |
| `ContractAccept` / `Complete` / `Fail` / `Cancel` | **no** | Sticky (KSP-owned). |
| `Milestone` | **no** | Sticky (KSP-owned first-time flag). |
| `FacilityUpgrade` / `Destruction` / `Repair` | **no** | Sticky. |
| `StrategyActivate` / `Deactivate` | **no** | Sticky. |
| `TechResearch` / `PartPurchase` | **no** | Sticky. |
| `FundsSpending` / `ScienceSpending` | **no** | Already-spent. |
| `ReputationEarning`, other rep | **no** | Not bundled with a death. |

Null-scoped actions (action's `RecordingId == null`) are NEVER tombstoned, regardless of type (§7.41).

Eligibility is checked with an idempotence guard: an action that already carries a tombstone (any existing `LedgerTombstone.ActionId == a.ActionId`) is skipped. This makes the whole merge re-runnable if the finisher drives through the Tombstone phase on load.

### 6.14 Revert-during-re-fly dialog

Files: `Source/Parsek/RevertInterceptor.cs` + `Source/Parsek/ReFlyRevertDialog.cs`.

`RevertInterceptor` is a pair of Harmony prefixes — one on `FlightDriver.RevertToLaunch` (Esc > Revert to Launch and the flight-results dialog's Launch revert button) and one on `FlightDriver.RevertToPrelaunch` (Esc > Revert to VAB / SPH and the flight-results dialog's VAB/SPH revert button). Both prefixes delegate to the shared dispatcher `RevertInterceptor.Prefix(RevertTarget target, EditorFacility facility)` where `RevertTarget ∈ {Launch, Prelaunch}` and `facility` is captured from the stock `RevertToPrelaunch(EditorFacility)` argument (ignored for the Launch path). When `ParsekScenario.Instance?.ActiveReFlySessionMarker == null`, the prefix returns `true` and the stock revert runs. When non-null, it returns `false` (blocking stock revert) and `ReFlyRevertDialog.Show(marker, target, onRetry, onFullRevert, onCancel)` spawns a `PopupDialog` with three buttons:

- **Retry from Rewind Point** — `RetryHandler`: clear marker, generate fresh `Guid`, re-invoke `RewindInvoker.StartInvoke(rp, slot)` with the same RP + slot captured from the marker. The old provisional becomes a zombie for the next load-time sweep to clean up. RP-anchored and scene-agnostic: Retry lands the player back in FLIGHT at the split moment regardless of whether they clicked Revert-to-Launch or Revert-to-VAB/SPH. Log `[ReFlySession] Info: End reason=retry sess=<old> rp=<rp> slot=<i> target=<Launch|Prelaunch>`.
- **Discard Re-fly** — `DiscardReFlyHandler`: session-scoped cleanup. Removes the provisional re-fly recording from `CommittedRecordings` (by-id lookup in the raw committed list, then `RecordingStore.RemoveCommittedInternal`; ERS-exempt because NotCommitted is invisible to ERS), promotes the origin RP (the RP named by `marker.RewindPointId`) by flipping `SessionProvisional=false` and clearing `CreatingSessionId` so the post-load `LoadTimeSweep` treats it as persistent (the sweep's RP-discard pass skips every `SessionProvisional=false` RP and every normal staging RP with `CreatingSessionId == null`), clears `ActiveReFlySessionMarker` and `ActiveMergeJournal`, bumps `SupersedeStateVersion` (not `TombstoneStateVersion` — no tombstone rows are touched), defensively deletes the prior session temp quicksave `saves/<save>/Parsek_Rewind_<sessionId>.sfs` if still present, then stages the RP quicksave through the same save-root copy path `RewindInvoker` uses and calls `GamePersistence.LoadGame(tempLoadName, SaveFolder, true, false)` to restore the RP's UT / career / vessels. On success, for the Prelaunch context pre-sets `EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN` (NOT `LOAD_FROM_CACHE` — the cached `ShipConstruct` is stale after `LoadGame` swaps `HighLogic.CurrentGame`) and `EditorDriver.editorFacility = facility`; then `HighLogic.LoadScene` dispatches to `GameScenes.SPACECENTER` for Launch or `GameScenes.EDITOR` for Prelaunch. Stock `FlightDriver.RevertToLaunch` / `RevertToPrelaunch` are NOT used — decompilation (via `ilspycmd` on `Assembly-CSharp.dll`) shows they restore the current flight's cached `PostInitState` / `PreLaunchState`, which are tied to the current flight's launch moment, not the RP's UT. `TreeDiscardPurge.PurgeTree` is NOT called: the tree's other Rewind Points, supersede relations, and tombstones are preserved. The origin split's crash recording plus its promoted RP still resolve, so `EffectiveState.IsUnfinishedFlight` stays true for `Immutable` legacy crashes and `CommittedProvisional` current crashes, together with the RP and a crashed terminal, and the Unfinished Flights row stays visible. Log `[ReFlySession] Info: End reason=discardReFly sess=<sid> target=<Launch|Prelaunch> facility=<VAB|SPH|-->` (the `facility=--` sentinel is logged in the Launch variant for grep-friendliness; the handler prepends `dispatched=false` and suppresses the scene call when the RP quicksave cannot be staged so the player is left in flight with a clear toast).
- **Continue Flying** — `CancelHandler`: pure logging; no state changes. Log `[ReFlySession] Info: Revert dialog cancelled sess=<sid> target=<Launch|Prelaunch>`.

Dialog body copy branches on `target`: the Launch variant says "returns you to the Space Center" as the Discard Re-fly destination; the Prelaunch variant says "returns you to the VAB or SPH" and explicitly clarifies that Retry still returns the player to FLIGHT at the split moment regardless of which Revert button they clicked. Title ("Revert during re-fly") and button labels are shared. When `ActiveMergeJournal` is non-null the Discard Re-fly button is omitted entirely (primary UX signal to the player that discard mid-merge is not allowed); the handler also refuses defensively with a `"merge in progress — retry in a moment"` toast + Warn log if called anyway. A third body hook (`ReFlyRevertDialog.BodyHookForTesting`) plus a buttons hook (`ReFlyRevertDialog.ButtonsHookForTesting`) let unit tests assert the variant + the journal gate without spawning a live popup.

The dialog takes an input lock (`DialogLockId = "ParsekReFlyRevertDialog"`) while visible so the player cannot interact with the flight scene during the decision.

`GameEvents.OnRevertToLaunchFlightState` / `OnRevertToPrelaunchFlightState` both fire from inside the respective stock body (after the state roll-back, before the scene unload). Because the interceptor's prefix returns `false` to block the body, neither event fires during an active session — `RevertDetector.PendingKind` stays `None` and `ParsekScenario.OnLoad`'s revert-aware branch is correctly skipped. `DiscardReFlyHandler` never re-calls the stock revert methods (it drives `LoadGame` + `LoadScene` directly), so `OnRevertToLaunchFlightState` stays un-fired across the whole revert flow; any post-load `RevertDetector` state has already been consumed (or is irrelevant) by the time the new scene's scenario loads.

### 6.15 Session end without merge

- **Return to Space Center** (without clicking Merge): `ParsekScenario.OnDestroy` / the merge-dialog-discard path clears the marker + discards the provisional + purges session-provisional RPs.
- **Quit without returning to Space Center**: marker stays on disk. Load-time validation (§6.16) decides what to do with it.

### 6.16 Load-time sweep

File: `Source/Parsek/LoadTimeSweep.cs`.

Runs from `ParsekScenario.OnLoad` AFTER `MergeJournalOrchestrator.RunFinisher` and `RewindInvoker.ConsumePostLoad`, and BEFORE `RewindPointReaper.ReapOrphanedRPs`. Single-pass gather-then-delete (eligibility decisions are made against a stable snapshot before any mutation):

1. **Marker validation** (`MarkerValidator.Validate`, `MarkerValidator.cs:86`). Six durable fields checked in order: `SessionId` non-empty; `TreeId` present in `RecordingStore.CommittedTrees`; `ActiveReFlyRecordingId` resolves in `CommittedRecordings` with `MergeState == NotCommitted`; `OriginChildRecordingId` resolves in `CommittedRecordings`; `RewindPointId` resolves in `ParsekScenario.RewindPoints`; `InvokedUT` is not strictly greater than current UT. The seventh field (`InvokedRealTime`) is informational only and does not fail a load. On any failure, `[ReFlySession] Warn: Marker invalid field=<f>; clearing` + marker cleared before the sweep continues (nested cleanup below depends on the cleared marker's session id).
2. **Spare set.** If marker valid: `ActiveReFlyRecordingId` and `RewindPointId` are in the spare set; additionally, any Recording with `CreatingSessionId == marker.SessionId` and any RP with `CreatingSessionId == marker.SessionId` join the spare set.
3. **Discard set.** Any Recording with `MergeState == NotCommitted` and not in the spare set is a zombie. Any RP with `SessionProvisional == true`, non-empty `CreatingSessionId`, and not in the spare set is a dead session-provisional RP. RPs with `CreatingSessionId == null` are normal staging RPs and are retained.
4. **Orphan supersede relations.** Walk `RecordingSupersedes`; log Warn for each relation whose `OldRecordingId` or `NewRecordingId` doesn't resolve. NOT removed — invariant 7.
5. **Orphan tombstones.** Walk `LedgerTombstones`; log Warn for each `ActionId` that doesn't resolve in `Ledger.Actions`. NOT removed.
6. **Stray `SupersedeTargetId`.** Walk committed recordings; for each `Immutable` or `CommittedProvisional` recording with non-empty `SupersedeTargetId`, log `[Recording] Warn: Stray SupersedeTargetId on committed rec=<id>; treating as cleared` and clear the field.
7. **Nested session-prov cleanup** (§7.11). When step 1 cleared the marker, count the discard-set entries that carried the cleared marker's session id and emit `[ReFlySession] Info: Nested session-prov cleanup sess=<sid> recordings=<n> rps=<m>`. (The discard set already caught them — this is an observational log for the §7.11 contract.)
8. **Delete.** Remove discard-set recordings from `RecordingStore`. Remove discard-set RPs from `ParsekScenario.RewindPoints` (and delete their quicksave files best-effort; a lock / IO failure Warns but still clears the scenario entry).
9. **Summary log.** Single `[LoadSweep] Info: Marker valid=<b>; spare=<n> discarded=<r> orphanSupersedes=<os> orphanTombstones=<ot> strayFields=<s> discardedRps=<rps>`.
10. **Cache invalidation.** `BumpSupersedeStateVersion` + `BumpTombstoneStateVersion` + `EffectiveState.ResetCachesForTesting` so ERS / ELS rebuild on next access.

### 6.17 Tree-scoped rewind-state purge

File: `Source/Parsek/TreeDiscardPurge.cs`.

Triggered by merge-discard (`RecordingStore.DiscardPendingTree`) or by any other path that discards a whole tree. Note: the Revert-during-re-fly dialog's Discard Re-fly button does NOT reach this path — design §6.14's `DiscardReFlyHandler` is session-scoped and leaves the tree's supersede / tombstone / RP state untouched. `TreeDiscardPurge.PurgeTree(treeId)` removes every Rewind-to-Separation artifact tied to the tree:

- Remove every RP whose `BranchPointId` is in the tree; delete each RP's quicksave file on disk.
- Remove every `RecordingSupersedeRelation` with either endpoint in the tree.
- Remove every `LedgerTombstone` whose target `GameAction.RecordingId` is in the tree.
- Clear `ActiveReFlySessionMarker` if it references the tree.
- Clear `ActiveMergeJournal` if it references the tree.
- Recompute crew reservations via `CrewReservationManager.RecomputeAfterTombstones` when tombstones shrank.

**What this does NOT do:** it does not remove the tree's committed recordings themselves and does not delete the tree from `RecordingStore.CommittedTrees`. Removal of the recordings (for a pending tree being discarded before commit) is the caller's responsibility — `TreeDiscardPurge.PurgeTree` is invoked by `RecordingStore.DiscardPendingTree` BEFORE the recordings are removed from `CommittedRecordings` so the purge can still resolve recording ids to tree membership. The Revert-during-re-fly dialog's Discard Re-fly button is explicitly NOT a caller of this purge — it only clears the session artifacts and reloads the origin RP's quicksave; the tree's other RPs / supersede relations / tombstones are preserved (design §6.14).

Purging a tree's rewind-state is the **only** path that deletes supersede relations or ledger tombstones (invariant 7). Orphans left by partial crash states stay in place and are logged on every load.

### 6.18 Disk-usage diagnostic

File: `Source/Parsek/RewindPointDiskUsage.cs`.

Surfaced in Settings → Diagnostics as `Rewind point disk usage: <size> (<N> files)`. Backed by a 10-second snapshot cache — the directory is walked once per cache miss, so the Settings window doesn't thrash the filesystem on every repaint.

---

## 7. Edge Cases

Each entry: scenario / expected behavior / shipped status. Status codes: **Shipped (test)** = dedicated unit / integration test; **Shipped (integration)** = covered by multi-test wiring or in-game test; **Deferred (v1 limitation)** = explicitly not in v1, documented; **N/A** = design evolved out. Test names name `*.cs` test classes in `Source/Parsek.Tests/`.

### 7.1 Single-controllable split
No RP. Debris path unchanged. **Shipped (test)**: `MultiControllableClassifierTests`.

### 7.2 Three or more controllable children at one split
One RP with N ChildSlots + N entries in each PID map. Player iterates through the slot rewind buttons. **Shipped (test)**: `RewindPointRoundTripTests` + `RewindPointAuthorTests`.

### 7.3 Scene-transition during deferred save
§6.6 step 1 of the deferred coroutine re-checks the scene guard; if we left FLIGHT, `RollbackBegin` removes the RP and clears the BranchPoint back-reference. Log `[RewindSave] Warn: Aborted mid-coroutine: scene=<loaded>`. **Shipped (test)**: `RewindPointAuthorTests`.

### 7.4 Partial PidSlotMap failure at split
One slot's lookup fails at split time; RP still created with partial maps. The affected slot is marked `Disabled = true` with reason `no-live-vessel`; other slots usable. Log `[Rewind] Warn: Slot <i> disabled: no live vessel`. **Shipped (test)**: `RewindPointAuthorTests`.

### 7.5 Invoking RP while a re-fly is active
UI + CanInvoke gate rejects with `"Another re-fly session is already active"`. No nested sessions in v1. **Shipped (test)**: `RewindTests` (precondition matrix).

### 7.6 Re-fly target with BG-recorded descendants
SessionSuppressedSubtree includes descendants (forward-only closure). Merge step 2 adds supersede relations for each descendant. **Shipped (test)**: `SessionSuppressionWiringTests` + `SupersedeCommitTests`.

### 7.7 Cross-tree dock during re-fly
Station `S` from a different tree's Immutable recording is real at its chain tip. The re-fly can physically dock. Dock event is added to the re-fly's recording. No cross-tree merge; S's tree is not modified. **Acceptable v1 limitation**: no dedicated in-game test for cross-tree dock; covered by `InvokeRPStripAndActivateTest` behavior when a docked station is present.

### 7.8 Non-Parsek stock vessel interaction
Standard — the stripper leaves unrelated vessels alone (§6.9 step 4). **Shipped (test)**: `PostLoadStripperTests`.

### 7.9 Visual divergence for cross-recording stock vessel
Ghost-B recorded a dock with S at UT=X; the live re-fly also docks with S at UT=X−100 and moves S. Ghost-B's playback tries to dock with S-where-ghost-B-remembers-it. Accepted v1 visual limitation.

### 7.10 EVA duplicate on load
Strip detects a kerbal present in EVA form AND in a source vessel's crew. Canonical live location is the selected child's crew manifest; dedup via KSP crew removal. **Shipped (integration)**: KSP's own reload logic handles the dedup; the stripper's ghost-ProtoVessel guard + left-alone path cover the remaining cases.

### 7.11 Nested session-provisional cleanup on parent discard
Nested RP's `CreatingSessionId` matches the discarded session; load-sweep spare-set logic removes it. **Shipped (test)**: `LoadTimeSweepTests`.

### 7.12 F5 mid-re-fly + quit + load
Marker validates; all session-tagged recordings AND RPs spared; re-fly resumes. Atomic §6.10 write means no save can capture an intermediate state. **Shipped (integration)**: in-game `F5MidReFlyResumeTest`.

### 7.13 Contract supersede is a no-op on career state
BG-crash completed contract X. Player re-flies, merges. ContractComplete action is NOT tombstoned (not v1-eligible). Contract remains complete in `ContractSystem`. Rep bonus stays. **Shipped (integration)**: `TombstoneEligibilityTests` + in-game `ContractStickyAcrossSupersedeTest`.

### 7.14 Contract failed by BG-crash; re-fly succeeds
BG-crash failed contract X. v1 does NOT un-fail. Contract stays failed. **Deferred (v1 limitation)**: re-fly is a visual replay, not a contract rescue.

### 7.15 Milestone earned by superseded recording
First-time flag is KSP-owned and sticky. v1 never un-sets. **Deferred (v1 limitation)**.

### 7.16 Kerbal death + recovery
`KerbalAssignment`-to-Dead + bundled `ReputationPenalty` tombstoned at merge. `CrewReservationManager.RecomputeAfterTombstones` re-derives; kerbal returns to active. **Shipped (integration)**: `CrewReservationRecomputeTests` + in-game `KerbalRecoveryOnSupersedeTest`.

### 7.17 Re-fly crashes; player merges
Re-fly commits as `CommittedProvisional` with `TerminalKind = Crashed` per `TerminalKindClassifier`. Supersede relation appends (`A → A'`). A' satisfies `IsUnfinishedFlight`. Player CAN invoke RP again to try once more. **Shipped (integration)**: in-game `MergeCrashedReFlyCreatesCPSupersedeTest`.

### 7.18 Re-fly reaches stable outcome; player discards at dialog
Provisional discarded; marker cleared; original BG-crash remains Unfinished; RP durable. **Shipped (integration)**: the discard path drops the provisional via `RecordingStore.RemoveCommittedInternal` + `ActiveReFlySessionMarker = null`.

### 7.19 Simultaneous multi-controllable splits
Two independent RPs; two quicksave writes serial through KSP. **Shipped (integration)**: `RewindPointAuthorTests` (deferred-one-frame ensures serialization through KSP's save pipeline).

### 7.20 F9 during re-fly
Standard quickload + auto-resume. Marker validates against the loaded state; if consistent, the session continues. **Shipped (integration)**.

### 7.21 Warp during re-fly
Standard Parsek warp. Not a new code path.

### 7.22 Rewind click during scene transition
CanInvoke returns `"Scene transition in progress — please wait"`. **Shipped (test)**: `RewindTests`.

### 7.23 Two children crash at same location
Independent rewinds via independent RPs.

### 7.24 Re-fly vessel with no engines
Allowed. The rewind only restores a quicksave state.

### 7.25 Drag Unfinished Flight into manual group
`GroupPickerUI.CanAddToUserGroup` rejects with `[UnfinishedFlights]` Verbose log + ScreenMessages toast. **Shipped (test)**: `UnfinishedFlightsDragRejectTests`.

### 7.26 Mod-modified joint-break / destruction
Classifier is `ModuleCommand`-based; mods that add alternative controller-like parts may not be seen. **Deferred (v1 limitation)** outside stock parts.

### 7.27 High physics warp at split
Forced to 0 during save (`RewindPointAuthor.ExecuteDeferredBody` step 2). **Shipped (integration)**: in-game `WarpZeroedDuringSaveTest`.

### 7.28 Disk usage diagnostics
Settings → Diagnostics shows total RP disk usage with a 10-second cache. **Shipped (test)**: `DiskUsageDiagnosticsTests`.

### 7.29 Mod part missing on re-fly load
Deep-parse precondition (`RewindInvoker.CanInvoke` step 6) walks the quicksave's PART nodes through `PartLoader.getPartInfoByName`. Any missing part marks the RP `Corrupted` and disables the Rewind button. **Shipped (test)**: `RewindTests` precondition matrix.

### 7.30 Cannot hide Unfinished Flights group
`GroupHierarchyStore.CanHide` denies. UI hide checkbox is not drawn for the virtual group. **Shipped (test)**: `UnfinishedFlightsGroupCannotHideTests`.

### 7.31 Non-crashed BG sibling (e.g. booster coasts to landing on its own)
Terminal kind classifies as `Landed` / `Orbiting` / `SubOrbital` / `InFlight` via `TerminalKindClassifier`. `IsUnfinishedFlight` returns **false**, so the recording does NOT enter the Unfinished Flights group and NO Rewind button is drawn on its row. **This is the intended behaviour, not a limitation.** The Rewind feature exists to let the player re-fly a sibling that ended in **destruction or loss**; a sibling that reached a stable terminal state (orbiting booster, landed parachuted kerbal, docked lander) has nothing to re-fly. The recording plays back from its committed trajectory and the end-of-recording spawn puts the real vessel where it actually ended up — exactly what the player intended when they last controlled it. Broadening the predicate to `CommittedProvisional + has-RP regardless of terminal` would pollute the list with every routine orbital separation (station EVAs, lander-from-orbit dockings, fuel-tanker rendezvous), which is the opposite of what the feature is for.

Corollary: if a sibling that **should** have been destroyed shows up with a non-crashed terminal (e.g. a booster left behind on reentry but misclassified `Orbiting`), the bug is in the terminal-state classifier, not the Rewind predicate. The scene-exit finalizer (`IncompleteBallisticSceneExitFinalizer` / `BallisticExtrapolator`) is the usual suspect; its `[Extrapolator]` log tag traces the decisions. A `Start: body=… alt=-<large>` preceded by `PatchedConicSnapshot: solver unavailable` (NullSolver) means KSP has already invalidated the vessel, and the extrapolator now short-circuits to `TerminalState.Destroyed` via `ExtrapolationFailureReason.SubSurfaceStart` rather than silently horizon-capping to Orbiting. Fixing the classifier upstream restores the Unfinished Flights entry automatically — do not work around it by broadening the UI predicate.

### 7.32 Classifier drift across Parsek versions
Old RPs retained; no reclassify. `[Rewind] Verbose` notes the mismatch.

### 7.33 Rename / hide on Unfinished Flight row
Rename persists through the generic `Recording.VesselName` path. Hide warns via `ScreenMessages` + refuses. **Shipped (test)**: `RenameOnUnfinishedFlightTests`.

### 7.34 Corrupted quicksave on invocation
RP marked Corrupted via `HandleQuicksaveMissing` at CanInvoke time (file missing) or via `PartLoaderPrecondition.MarkCorrupted` (parse failure / missing parts). Rewind button disabled with the precondition reason. **Shipped (test)**: `RewindTests`.

### 7.35 Auto-save concurrent with RP save
One-frame defer + root-save-then-move + KSP's serial save pipeline. Auto-save is blocked during the synchronous `GamePersistence.SaveGame` inside the deferred body. **Shipped (integration)**.

### 7.36 Rewind while unrelated new mission in-flight
CanInvoke gate rejects if a session is already active; if the player is flying a new unrelated mission outside any session, they must first Revert-to-Launch / return-to-Space-Center that mission. **Shipped (integration)**: `RewindTests`.

### 7.37 Pre-existing vessel reservation conflict during re-fly
`CrewReservationManager.IsLiveReFlyCrew` carve-out exempts the live re-fly crew from reservation lock for the session duration. **Shipped (test)**: `SessionSuppressionWiringTests` + in-game `KerbalDualResidenceCarveOutTest`.

### 7.38 Strip encounters ghost ProtoVessel
`PostLoadStripper.Strip` step 1 ghost guard via `GhostMapPresence.IsGhostMapVessel`. **Shipped (test)**: `PostLoadStripperTests`.

### 7.39 Strip encounters unrelated committed real vessel
Step 4 left-alone path. Log `[Rewind] Verbose: Strip leaveAlone: unrelated v=<pid>`. **Shipped (test)**: `PostLoadStripperTests`.

### 7.40 SessionSuppressedSubtree with mixed-parent descendant
Closure halts at the mixed-parent node. Descendant stays in ERS. **Shipped (test)**: `EffectiveStateTests` (mixed-parent halt case).

### 7.41 Null-scoped action in supersede scope
v1 tombstones only when the action's `RecordingId` is in the supersede subtree. Null-scoped actions (deadline `ContractFail`, stand-in generation, etc.) are NEVER tombstoned. They stay in ELS. **Shipped (test)**: `TombstoneEligibilityTests`.

### 7.42 KSC action during active re-fly
Player doesn't typically leave flight during re-fly. If a KSC action fires (mod or edge case), it's added to the ledger with null `RecordingId`; not in supersede scope; stays ELS. **Shipped (integration)**.

### 7.43 Chain extends through multiple merged crashes
Each `CommittedProvisional Crashed` merge extends the supersede chain. `EffectiveRecordingId` walks forward through all relations. Unfinished Flights moves along with the chain's tip. **Shipped (test)**: `ChildSlotEffectiveRecordingIdTests` exercises the forward walk through 0/1/2/3-length chains; the chain-extension flow over multiple merges is covered by the in-game `MergeCrashedReFlyCreatesCPSupersedeTest` matrix.

### 7.44 Vessel-destruction rep penalty
Original BG-crash had a vessel-destruction rep penalty (not kerbal-death; just "vessel blew up"). v1 does NOT tombstone general `ReputationPenalty` actions — only the kerbal-death-bundled rep retires. Vessel-destruction rep stays. **Deferred (v1 limitation)**.

### 7.45 Merge interruption recovery
Journaled commit + finisher triggered by journal presence on load, regardless of marker state. Five distinct crash windows covered. **Shipped (test)**: `MergeCrashRecoveryMatrixTests` + `MergeJournalOrchestratorTests` + in-game `MergeInterruptionRecoveryTest` / `JournalFinisherMarkerPresentVariantTest`.

### 7.46 EffectiveRecordingId walk direction + orphan handling
Walks forward via `RecordingSupersedes`. Cycle detection via visited set (`EffectiveState.cs:88`). One-sided orphan relation (new endpoint missing) is treated as a chain terminator at prior node and logs Warn. Fully orphaned rows are removed by load-time sweep before they can warn forever. **Shipped (test)**: `EffectiveStateTests` + `ChildSlotEffectiveRecordingIdTests`.

### 7.47 Orphan supersede relation on load
Step 4 of load sweep logs Warn and retains one-sided orphans; fully orphaned rows (`oldResolved=false && newResolved=false`) are removed because neither endpoint can affect effective state. Walk treats retained one-sided orphans as terminators. **Shipped (test)**: `LoadTimeSweepTests`.

### 7.48 Immutable-merged re-fly — player wants to retry anyway
They cannot. `Immutable` seals the slot. To retry, the player must Full-Revert, which clears the rewind point + supersede / tombstone state for the tree (the committed recordings stay in the timeline). **Shipped (integration)**: documented behavior + `TreeDiscardPurgeTests` for the full-revert path.

---

## 8. What Doesn't Change

- **Flight-recorder principle 10** (additive-only recording tree). Enforced rigorously: supersede is a relation, not a field; tombstones are append-only; no field on a committed Recording is ever written post-commit; orphan supersede relations are never deleted outside tree-discard.
- **Single-controllable split path.** Unchanged.
- **Merge dialog UI shell.** Unchanged; v1 adds a supersede-advisory line when an active session is present.
- **Ghost playback engine.** Reads ERS via `SessionSuppressionState`; `SessionSuppressedSubtree` is upstream of the engine, not inside it.
- **Active-vessel ghost suppression.** Natural ERS filter (`NotCommitted` recordings are excluded; the live provisional is `NotCommitted`).
- **Ledger recalculation engine.** Reads ELS via `EffectiveState.ComputeELS`; no engine change.
- **Rewind-to-launch (the existing T0 quicksave-reload feature).** Unchanged path.
- **Loop / overlap / chain.** Read ERS through the same filter; no per-feature code change.
- **Recording sidecar format.** No changes.
- **Reservation manager internals.** Re-derivation from ERS is existing; the carve-out in `IsLiveReFlyCrew` is a single-method filter.
- **Career state (KSP-sticky subsystems).** v1 never touches `ContractSystem`, `ProgressTracking`, `ScenarioUpgradeableFacilities`, `StrategySystem`, `ResearchAndDevelopment`, or the vessel-destruction rep code paths on supersede. Contracts completed by the superseded recording stay complete. Milestones earned stay earned. Facility upgrades stay upgraded. Tech researched stays researched.
- **Vessel-destruction reputation.** Not a tombstone-eligible type. The `ReputationPenalty` action from a vessel destruction in a superseded subtree stays in ELS; career rep is unchanged by supersede unless a kerbal also died.
- **Science rewards.** `ScienceSpending` and `ScienceEarning` stay in ELS.
- **Legacy saves.** `GameAction.ActionId` is auto-generated via deterministic hash on first post-upgrade load; `MergeState` migrated from the binary legacy flag; new ConfigNode sections (`REWIND_POINTS`, `RECORDING_SUPERSEDES`, `LEDGER_TOMBSTONES`, `REFLY_SESSION_MARKER`, `MERGE_JOURNAL`) are optional and absent on pre-v0.9 saves.
- **Standalone ghost playback mod (Gloops).** The feature touches only Parsek-internal code; nothing the engine depends on moves.
- **Existing chain semantics outside a re-fly session.** BranchPoints, chain tips, ghost extension, trajectory walkback, etc. are read-only consumers of the new ERS view; the session-suppressed subtree only takes effect while a marker is live.

---

## 9. Backward Compatibility

### 9.1 Pre-v0.9 saves on v0.9+

- Load cleanly. All five new ConfigNode sections are absent → treated as empty lists / null singletons (`REWIND_POINTS`, `RECORDING_SUPERSEDES`, `LEDGER_TOMBSTONES` → `List<T>()`; `REFLY_SESSION_MARKER`, `MERGE_JOURNAL` → `null`).
- `Recording.MergeState` legacy migration: binary `committed = True` → `Immutable`; `committed = False` → `NotCommitted`; absent → `Immutable` (pre-feature invariant: any recording reachable from a committed tree was already sealed). One-shot `[LegacyMigration] Info` log line on first post-upgrade load.
- `GameAction.ActionId` legacy migration: deterministic hash `act_<hash>` generated from `UT + Type + RecordingId + Sequence` (`GameAction.cs:541`). Idempotent — the same action always yields the same id. One-shot `[LegacyMigration] Info` log line.
- `Recording.SupersedeTargetId` — never set on pre-v0.9 saves. Non-empty values on `Immutable` / `CommittedProvisional` recordings (should only happen on a corrupt or hand-edited save) log Warn and are treated as cleared (`LoadTimeSweep.ClearStraySupersedeTargets`, `LoadTimeSweep.cs:326`).
- `BranchPoint.RewindPointId` — absent on pre-v0.9 saves. No `IsUnfinishedFlight` predicate matches on pre-feature BG-crashed siblings because the predicate requires a non-null parent `RewindPointId`. The player's existing crashed siblings are NOT retroactively made Unfinished Flights.
- Pre-v0.9 recordings have no cargo/crew manifest changes; Rewind to Separation does not read or write those fields.

### 9.2 Post-v0.9 saves on v0.8.x

**Not supported.** A post-v0.9 save carries `REWIND_POINTS` / `RECORDING_SUPERSEDES` / `LEDGER_TOMBSTONES` / `REFLY_SESSION_MARKER` / `MERGE_JOURNAL` nodes that pre-v0.9 Parsek does not know. Depending on the KSP version's ConfigNode parser tolerance, the save may:
- Load cleanly but silently drop the new sections on next save (losing supersede relations → superseded recordings reappear in ERS).
- Fail to load with a KSP-side parse error.

No downgrade path is provided.

### 9.3 Feature removal

All new entities are ignorable as orphans. Removing the feature from a save would leave `REWIND_POINTS` etc. unused; the shipped code does not offer a "strip rewind state" toggle.

---

## 10. Known Limitations / Future Work

### 10.1 ERS-exempt consumers (index-keyed state)

Thirteen files are on the ERS-audit allowlist (`scripts/ers-els-audit-allowlist.txt`) with inline `[ERS-exempt — Phase 3]` comments and `TODO(phase 6+)` markers. These hold raw index-based state keyed by positions in `RecordingStore.CommittedRecordings`:

- `Source/Parsek/GhostMapPresence.cs`
- `Source/Parsek/WatchModeController.cs`
- `Source/Parsek/ChainSegmentManager.cs`
- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/ParsekPlaybackPolicy.cs`
- `Source/Parsek/ParsekTrackingStation.cs`
- `Source/Parsek/Diagnostics/DiagnosticsComputation.cs`
- `Source/Parsek/FlightRecorder.cs`
- `Source/Parsek/ParsekKSC.cs`
- `Source/Parsek/ParsekUI.cs`
- `Source/Parsek/UI/RecordingsTableUI.cs`
- `Source/Parsek/UI/GroupPickerUI.cs`
- `Source/Parsek/UI/SettingsWindowUI.cs`

The follow-up work is a recording-id-keyed refactor so the remaining grep-audit exemptions can be lifted. Each exemption has a call-site comment explaining why the raw index read is currently required. The refactor is structurally large (touches every subsystem that keys on committed-recordings index) and deferred beyond v0.9.

### 10.2 Background-split RP capture relies on cached PID payload

For BG-recorded splits that originate outside the focus vessel, the RP author consumes a cached PID payload produced by `BackgroundRecorder`. A joint-break during high warp on a truly unloaded vessel may not emit the event, in which case no RP is captured and the BG-crashed sibling cannot be rewound. Covered by `BackgroundSplitRpTests` for the supported paths; edge cases with on-rails breakups remain deferred work.

### 10.3 Wider tombstone scope (v2)

Candidate types beyond `KerbalAssignment`+Dead and bundled rep:

- Vessel-destruction `ReputationPenalty` (unbundled from kerbal deaths). Needs confirmation that KSP-side rep can be safely refunded without re-emitting a destruction event.
- Science-loss on destruction. Parsek-owned, should be tombstone-safe.
- Contract completion / failure for cases where the KSP-side `ContractSystem` can be scripted to re-emit on re-fly. Probably not possible without invasive Harmony work.
- Milestone flags for `ProgressTracking` categories where the binary flag can be safely cleared and re-emitted.

Each candidate needs per-type confirmation that KSP-side state can either be re-emitted or is purely Parsek-owned.

### 10.4 Cross-tree supersedes

`EffectiveState.EffectiveRecordingId` has an inline TODO (`EffectiveState.cs:76`) to halt the walk at cross-tree boundaries once cross-tree supersedes become producible. v1 does not produce them; the invariant is enforced by the merge logic (supersede relations are scoped to the session's tree).

### 10.5 End-to-end automated in-game RunInvoke test

Stubbed. Scene reload is not drivable under xUnit, and the KSP in-game test runner does not yet support the full Restore → Strip → Activate → AtomicMarkerWrite pipeline as a single test. The in-game `InvokeRPStripAndActivateTest` covers the strip + activate portion; the pre-load copy-to-root / scene reload is exercised only via manual playtest.

### 10.6 Auto-purge policies for reap-eligible RPs

There are none in v1. The disk-usage diagnostic surfaces the current total; the player can Wipe All Recordings to clear them. A scheduled purge for old reap-eligible RPs (TTL-based) is future work.

### 10.7 Recovery-snapshot feature

The pre-impl doc's `SplitTimeSnapshot` concept (`_split.craft` sidecar, redundant to the quicksave) was dropped before v1. A concrete recovery mechanism for corrupt quicksaves is deferred; v1's Corrupted flag keeps the RP visible for diagnostic purposes only.

### 10.8 Player-selectable merge mode for crashes

v1 auto-picks `CommittedProvisional` for crash outcomes per `TerminalKindClassifier`. v2 could offer "commit as final (no further rewind)" for players who want to seal the slot and accept the crash.

### 10.9 SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT

A pre-existing Unity-gated test skip unrelated to Rewind to Separation, but exercised heavily by the feature's in-game tests. Carried forward as an open follow-up from earlier phases.

---

## 11. Diagnostic Logging

Every decision point in §6 emits a log line. The tag catalog below enumerates what appears in `KSP.log` for a Rewind-to-Separation session. **All tags are verified against the shipped source** in the files named in §1.1. The pre-impl spec's candidate tags `Reap`, `Strip`, and `Tombstone` did NOT ship as distinct tag names — their log lines fold into `[Rewind]` and `[LedgerSwap]` respectively. `RewindSave`, `ERS`, and `ELS` DID ship as distinct tags and are cataloged below.

### 11.1 `[Rewind]`

Broad tag for RP lifecycle and strip outcomes. Used by `RewindPointAuthor`, `RewindInvoker`, `PostLoadStripper`, `RewindPointReaper`, `TreeDiscardPurge`, `LoadTimeSweep`, `RewindPointDiskUsage`.

- RP creation: `Info: RewindPoint begin: rp=<id> bp=<bpId> slots=<N> controllablePids=<M> ut=<x>`.
- Slot capture summary: `Info: RewindPoint slot capture: rp=<id> populated=<p>/<total> disabled=<d>`.
- Partial capture warning: `Warn: Slot <i> disabled: no live vessel for rec=<rid> (rp=<id>)`.
- All-slot failure: `Warn: All slots disabled; RP unusable (rp=<id>). Keeping for diagnostic visibility.`
- Rollback: `Info: RewindPoint rolled back: rp=<id> removals=<n>`.
- Strip: `Info: Strip stripped=[...] selected=<idx> ghostsGuarded=<N> leftAlone=<M> fallbackMatches=<f>`.
- Strip fallback match: `Warn: Fallback match via root-part v=<pid> rootPart=<rootPid> slotIdx=<idx>`.
- Strip leave-alone: `Verbose: Strip leaveAlone: unrelated v=<pid> name=<n>`.
- Strip ghost guard: `Verbose: Strip guard: ghost-ProtoVessel v=<pid> name=<n>`.
- Reaper: `Info: ReapOrphanedRPs: reaped=<R> remaining=<rem>`. `Verbose: ReapOrphanedRPs: reaped=0 remaining=<n>` when nothing to do.
- Tree discard: `Info: TreeDiscardPurge: tree=<id> removedRps=<r> removedSupersedes=<s> removedTombstones=<t>`.
- Load summary: `Info: [LoadSweep] Marker valid=<b>; spare=<n> discarded=<r> ...` (see `[LoadSweep]`).
- Disk usage Warn (directory walk failure): `Warn: RewindPointDiskUsage directory walk failed: <reason>`.

### 11.2 `[RewindSave]`

Narrower tag for the RP save pipeline. Used by `RewindPointAuthor`.

- Scene abort: `Warn: Aborted: scene=<loaded>`.
- Deferred-coroutine abort: `Warn: Aborted mid-coroutine: scene=<loaded>`.
- Warp drop: `Info: Warp dropped from <prior> to 0 for rp=<id>`.
- Warp restore: `Info: Warp restored to <rate> for rp=<id>` / `Warn: Warp restore to <rate> failed for rp=<id>: <reason>`.
- Save success: `Info: Wrote rp=<id> path=<relPath> bytes=<N> ms=<t>`.
- Save failure: `Error: Failed rp=<id> reason=<…>`.

### 11.3 `[RewindUI]`

UI-layer decisions. Used by `RewindInvoker.ShowDialog` and `ReFlyRevertDialog`.

- Invoke button clicked: `Info: Invoked rec=<rid> rp=<rpId> slot=<idx> listIndex=<i>`.
- Dialog cancelled: `Info: Cancelled rp=<rpId> slot=<idx> listIndex=<i>`.
- ReFlyRevertDialog input lock: `Verbose: ReFlyRevertDialog input lock set (<lockId>)` / `... cleared`.
- Null callbacks on dialog buttons: `Warn: ReFlyRevertDialog: <Button> button had null callback sess=<sid>`.
- Callback throws: `Error: ReFlyRevertDialog <Button> callback threw: <Type>: <message>`.

### 11.4 `[ReFlySession]`

Session lifecycle. Used by `RewindInvoker`, `SessionSuppressionState`, `MergeJournalOrchestrator`, `LoadTimeSweep`, `RevertInterceptor`, `ReFlyRevertDialog`, `TreeDiscardPurge`.

- Session start: `Info: Started sess=<sid> rp=<id> slot=<idx> provisional=<rid> origin=<oid> tree=<tid>`.
- Session-suppressed subtree Start/End: `Verbose: SuppressedSubtree entered sess=<sid> ids=[...]` / `... exited`.
- Retry: `Info: Retry sess=<old> → <new>` (issued by `RevertInterceptor.RetryHandler`).
- End (merged): `Info: End reason=merged sess=<sid> provisional=<rid>`.
- End (full revert): `Info: End reason=fullRevert sess=<sid>`.
- Revert dialog shown: `Info: Revert dialog shown sess=<sid>`.
- Revert dialog cancelled: `Info: Revert dialog cancelled sess=<sid>`.
- Marker valid on load: `Info: Marker valid sess=<sid> tree=<tid> active=<rid> origin=<oid> rp=<rpid>`.
- Marker invalid on load: `Warn: Marker invalid field=<f>; clearing`.
- Nested cleanup: `Info: Nested session-prov cleanup sess=<sid> recordings=<n> rps=<m>`.

### 11.5 `[Supersede]`

Supersede relation writes + ERS-effective observations. Used by `SupersedeCommit`, `EffectiveState`, `LoadTimeSweep`, `TreeDiscardPurge`.

- Relation append: `Info: rel=<relId> old=<oid> new=<nid>`.
- Subtree count summary: `Info: Added <N> supersede relations for subtree rooted at <originId> (subtreeCount=<sc> skippedExisting=<se>)`.
- Flip MergeState: `Info: provisional=<rid> mergeState=<state> terminalKind=<kind> priorTarget=<oid>`.
- Advisory: `Info: Narrow v1: physical playback replaced; career state unchanged except kerbal deaths`.
- Cycle detection: `Warn: EffectiveRecordingId: cycle detected in supersede chain starting from <id> ...`.
- Orphan relation on load: `Warn: Orphan supersede relation=<relId> oldResolved=<b> newResolved=<b>`.
- Fully orphaned relation on load: `Info: Fully orphaned relation=<relId> old=<oid> new=<nid>; removing`.
- Tree-discard purge count: `Info: PurgeTree: removed <N> supersedes with endpoint in tree=<id>`.

### 11.6 `[LedgerSwap]`

Tombstone writes + reservation-recompute diagnostics. Used by `SupersedeCommit`, `LoadTimeSweep`, `TreeDiscardPurge`.

- Tombstone batch: `Info: Tombstoned <N> actions (KerbalDeath=<d> repBundled=<r>); <M> actions matched subtree but type-ineligible (breakdown: contract=<c> milestone=<m> facility=<f> strategy=<s> tech=<t> science=<sc> funds=<fn> rep-unbundled=<ru>)`.
- Skip existing: `Verbose: skip existing tombstone action=<aid>`.
- Reservation recompute: `Info: CrewReservation re-derived; kerbals returned active: [names]`.
- Orphan tombstone on load: `Warn: Orphan tombstone=<tid> actionId=<aid>`.

### 11.7 `[MergeJournal]`

Staged-commit phase transitions + finisher outcomes. Used by `MergeJournalOrchestrator`, `TreeDiscardPurge`.

- Phase start (Begin): `Info: sess=<sid> phase=Begin`.
- Phase advance: `Verbose: sess=<sid> phase=<phase>`.
- Fault injection (tests only): `Warn: Fault injected at phase=<Phase>`.
- Finisher: `Info: Finisher sess=<sid> markerWas=<present|absent> phase=<phase>`.
- Rollback outcome: `Info: Rolled back from phase=<p>: session restored sess=<sid> markerCleared=<b> provisionalRemoved=<r>`.
- Completion outcome: `Info: Completed from phase=<p> sess=<sid> stepsDriven=<n>`.
- Final cleared: `Info: Completed sess=<sid>`. `Verbose: sess=<sid> cleared`.
- TagRpsForReap: `Info: TagRpsForReap: promoted <n> session-provisional RP(s) for sess=<sid>`.
- Rollback recording removal: `Info: Rollback: removed session-provisional recording id=<rid>`.

### 11.8 `[LoadSweep]`

Single summary line per load after `LoadTimeSweep.Run` completes.

- `Info: [LoadSweep] Marker valid=<b>; spare=<n> discarded=<r> orphanSupersedes=<os> orphanTombstones=<ot> strayFields=<s> discardedRps=<rps>`.
- Pre-summary Verbose: `Verbose: LoadTimeSweep.Run: no ParsekScenario instance — skipping` when no scenario live.

### 11.9 `[UnfinishedFlights]`

Classifier + UI group decisions. Used by `EffectiveState` and `UnfinishedFlightsGroup`.

- Positive classification: `Verbose: IsUnfinishedFlight=true rec=<rid> bp=<bpId> rp=<rpid>`.
- Negative with reason: `Verbose: IsUnfinishedFlight=false rec=<rid> reason=<…>` (reasons: `mergeState:<state>`, `notCrashed:<terminal>`, `noParentBp`, `noScenario`, `noMatchingRP bp=<bpId> rpCount=<n>`).
- UI drag-into rejection: Verbose log + `ScreenMessages` toast (`Unfinished Flights is a system group and cannot receive dropped recordings`).

### 11.10 `[CrewReservations]`

Tree-discard reservation recomputation. Used by `TreeDiscardPurge`.

- Re-derive after tombstone set shrunk: `Info: Reservation re-derived after tree discard tree=<id>`.
- No-op: `Verbose: PurgeTree: no reservations affected tree=<id>`.

### 11.11 `[RevertInterceptor]`

Harmony prefix decisions. Used by `RevertInterceptor.Prefix`.

- Pass-through (no session): `Verbose: Prefix: no active re-fly session — allowing stock RevertToLaunch`.
- Block: `Info: Prefix: blocking stock RevertToLaunch sess=<sid> — showing re-fly dialog`.
- Error paths: `Error: ReflyRevertInterceptor internal failure: <reason>`.

### 11.12 `[ReconciliationBundle]`

Invocation Restore / Capture observations. Used by `ReconciliationBundle`.

- Capture: `Info: Captured bundle: recordings=<n> trees=<m> ledgerActions=<l> supersedes=<s> tombstones=<t> rps=<rps> marker=<b> journal=<b>`.
- Restore: `Info: Restored bundle: <same fields>`.

### 11.13 `[ERS]` / `[ELS]`

Cache rebuilds. Used by `EffectiveState`.

- ERS rebuild: `Verbose: Rebuilt: <n> entries from <raw> committed (skippedNotCommitted=<k> skippedSuperseded=<s> skippedSuppressed=<u> marker=<sessionId|none>)`.
- ELS rebuild: `Verbose: Rebuilt: <n> entries from <raw> (skippedTombstoned=<k>)`.

### 11.14 `[Recording]`

Stray-field cleanup on load. Used by `LoadTimeSweep`.

- `Warn: Stray SupersedeTargetId on committed rec=<id>; treating as cleared`.

### 11.15 `[Boundary]`

Multi-controllable classifier decisions. Used by `SegmentBoundaryLogic`.

- Classification outcome: `Info: ClassifyJointBreakResult: <outcome> originalPid=<pid> postBreakCount=<n>`.

---

## 12. Testing

### 12.1 Unit tests

The Rewind-scoped test classes in `Source/Parsek.Tests/`:

- `ActionIdMigrationTests` — deterministic `ActionId` generation from legacy action fields; idempotence.
- `AtomicMarkerWriteTests` — `RewindInvoker.AtomicMarkerWrite` including the `Phase1And2_NoOnSaveBetween` atomicity guard.
- `BackgroundSplitRpTests` — BG-split RP capture with cached PID payload.
- `BranchPointRewindPointIdRoundTripTests` — BranchPoint.RewindPointId serialization.
- `ChildSlotEffectiveRecordingIdTests` — forward walk with cycle guard + orphan terminator.
- `CopyRewindSaveToRootTests` — quicksave copy to save-root for `GamePersistence.LoadGame`.
- `CrewReservationRecomputeTests` — dead kerbal returns active after tombstone; no-op-safe paths.
- `DiskUsageDiagnosticsTests` — directory walk, missing-dir zero fallback, 10s cache TTL.
- `EffectiveStateTests` — ERS/ELS computation, mixed-parent halt, IsVisible, IsUnfinishedFlight, EffectiveRecordingId.
- `GrepAuditTests` — CI gate: verifies the `scripts/grep-audit-ers-els.ps1` allowlist matches the current source.
- `LedgerTombstoneRoundTripTests` — tombstone serialization.
- `LegacyMigrationTests` — MergeState migration + ActionId migration idempotence.
- `LoadTimeSweepTests` — marker validation, spare/discard sets, orphan logs, stray field, nested cleanup, summary.
- `MergeCrashRecoveryMatrixTests` — per-window state-invariant assertions for the 5-point crash matrix.
- `MergeJournalOrchestratorTests` — happy path + rollback + completion + idempotence + null-guard.
- `MergeJournalRoundTripTests` — MergeJournal ConfigNode round-trip.
- `MultiControllableClassifierTests` — `IsMultiControllableSplit` gate coverage.
- `PostLoadStripperTests` — ghost guard, primary match, fallback match, unrelated left alone, multi-slot conflict.
- `ReFlyRevertDialogTests` — Retry / Discard Re-fly / Cancel handlers + null callback paths; Discard Re-fly covers session-artifact clear, origin-RP promotion, journal-gate + defensive refusal, scene dispatch to `SPACECENTER` (Launch) or `EDITOR` with `EditorDriver.StartupBehaviour=START_CLEAN` + facility (Prelaunch), and survival of the origin RP across `LoadTimeSweep`.
- `ReFlySessionMarkerRoundTripTests` — marker ConfigNode round-trip.
- `ReconciliationBundleTests` — capture + restore over every field.
- `RecordingSupersedeRelationRoundTripTests` — relation ConfigNode round-trip.
- `RenameOnUnfinishedFlightTests` — rename persistence + hide-row advisory.
- `RewindCleanupClearTests` / `RewindContextTests` / `RewindLoggingTests` / `RewindTests` / `RewindTimelineTests` / `RewindTreeLookupTests` / `RewindUtCutoffTests` — overall invocation flow, log capture, tree lookup, UT-boundary behaviors.
- `RewindPointAuthorTests` — scene guard, warp drop, PID map capture, partial failure.
- `RewindPointReaperTests` — eligibility matrix, supersede walk, orphan slot, file-delete failure, idempotence.
- `RewindPointRoundTripTests` — RP ConfigNode round-trip including both PID maps.
- `RewindPrelaunchStripTests` — PRELAUNCH guard interactions.
- `SessionSuppressionWiringTests` — predicate + transition logging + chain-walker skip + map-presence gate + crew carve-out.
- `SpawnStateReconciliationTests` — post-strip state reconciliation.
- `SupersedeCommitTests` / `SupersedeCommitTombstoneTests` — commit + tombstone matrix + subtree + mixed-parent halt + idempotence.
- `TombstoneEligibilityTests` — full type matrix + 1s pairing window.
- `TreeDiscardPurgeTests` — RPs + supersedes + tombstones + reservations + marker/journal + sibling-tree isolation + state-version bumps.
- `UnfinishedFlightsDragRejectTests` / `UnfinishedFlightsGroupCannotHideTests` / `UnfinishedFlightsMembershipTests` — UI group invariants.
- `EarningsReconciliationTests` — earnings-ledger interactions with tombstoned rep bundles.

### 12.2 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`, Ctrl+Shift+T)

- `CaptureRPOnStaging` — asserts both PID maps populated, quicksave exists in `Parsek/RewindPoints/`.
- `SavePathRootThenMove` — no stray file left in save root.
- `WarpZeroedDuringSave` — warp drops to zero during save.
- `InvokeRPStripAndActivate` — strip counts + active-vessel PID unchanged.
- `MergeLandedReFlyCreatesImmutableSupersedeTest` — relation in list, provisional Immutable, RP reaps.
- `MergeCrashedReFlyCreatesCPSupersedeTest` — provisional `CommittedProvisional`, is Unfinished Flight, RP does not reap; re-invoke path exercises chain extension on next merge.
- `ContractStickyAcrossSupersedeTest` — contract completed by BG-crash stays complete after re-fly merge.
- `GhostSuppressionDuringReFlyTest` — no ghost rendering for supersede-target subtree.
- `KerbalRecoveryOnSupersedeTest` — kerbal returns active; reservation re-derived.
- `KerbalDualResidenceCarveOutTest` — live re-fly crew exempt from reservation lock.
- `UnfinishedFlightsRenderingAndNoHideTest` — UI group renders correctly and cannot be hidden.
- `F5MidReFlyResumeTest` — atomic phase 1+2 means marker validates; spare set survives.
- `MergeInterruptionRecoveryTest` — inject exception mid-merge; verify finisher behavior.
- `JournalFinisherMarkerPresentVariantTest` — marker-present variant of the finisher.
- `TreeDiscardRemovesSupersedesAndTombstonesTest` — tree-discard purge covers all scoped collections.
- `RPReapOnLastSlotImmutableTest` — RP reaps when the last slot becomes Immutable.
- `PartPersistentIdStabilityTest` — critical precondition probe: `Part.persistentId` is stable across the save/load cycle (required for `RootPartPidMap` fallback to work).

### 12.3 Grep-audit CI gate

- `scripts/grep-audit-ers-els.ps1` scans every source file outside the allowlist for raw `RecordingStore.CommittedRecordings` or `Ledger.Actions` reads.
- `scripts/ers-els-audit-allowlist.txt` enumerates the 35 permitted exemptions with rationale comments. Each exemption has an inline `[ERS-exempt]` comment at the call site.
- `GrepAuditTests.GrepAudit_AllRawAccessIsAllowlisted` fails the build on any un-allowlisted raw read.

### 12.4 Crash-recovery reviewer sign-off

The pre-impl spec's v0.5 sign-off condition was "crash recovery matrix covers every window." `MergeCrashRecoveryMatrixTests` asserts per-window state invariants for the 5 crash-point cases; `MergeJournalOrchestratorTests` exercises the rollback and completion paths via `FaultInjectionPoint` + `DurableSaveForTesting` seams. Together they cover every pre-Durable-1 and post-Durable-1 crash window referenced in §6.12's failure-recovery matrix.

---

## Appendix A — Gameplay Scenarios

### A.1 Booster recovery (the motivating case)

1. Player launches an AB stack: upper stage B (crewed capsule) on top of booster A (probe core + parachute, intended for recovery).
2. Staging: the decoupler fires. A and B split. Both controllable → RP-1 is captured (one-frame-deferred quicksave under `Parsek/RewindPoints/`). PidSlotMap records B and A by Vessel.persistentId; RootPartPidMap records their root-part PIDs.
3. Player flies B to orbit. A is BG-recorded; the player gets no chance to deploy the chute in time and A crashes into the ocean.
4. Player returns to Space Center. Merge dialog → Merge to Timeline. Tree commits: ABC Immutable, B Immutable (Orbited), A CommittedProvisional (Crashed, terminal kind classifies as Crashed). RP-1 survives the scene load as a normal staging RP (`SessionProvisional=true`, `CreatingSessionId=null`) and is promoted to persistent when the tree commits. Recordings Manager shows a new "Unfinished Flights" virtual group containing A with a Rewind button.
5. Player opens Recordings Manager, finds A under Unfinished Flights, clicks **Rewind**. Dialog names the split UT and advisory about career state / kerbal deaths. Player clicks **Rewind**.
6. Scene reloads into FLIGHT at the staging moment. A is the active vessel (live physics); B has been stripped and now plays back as a ghost from its committed recording. ABC plays back as a ghost up to the split moment, then terminates. Player's career state matches what it was immediately before Rewind (B's orbit entry + all milestones / contracts are intact).
7. Player deploys A's parachute and lands it safely on the launch pad. Recorder finalizes A' with TerminalKind=Landed.
8. Merge dialog → Merge to Timeline. `MergeJournalOrchestrator.RunMerge` drives through the nine phases. A → hidden (superseded); A' → Immutable + effective slot-1 representative. No tombstones (no kerbal died). RP-1 reap-eligible (both slots Immutable) → quicksave deleted, scenario entry removed, BranchPoint back-reference cleared.
9. Unfinished Flights is now empty. The timeline shows B's orbit entry AND A's safe landing (the sealed slot points at `A'` via the supersede relation); both play back on any subsequent rewind-to-launch.

### A.2 EVA re-fly

1. Player has a Mun lander in orbit. Kerbal EVAs to plant a flag.
2. Multi-controllable split (EVA): lander + kerbal. RP-2 captured.
3. Kerbal slips off the ladder while re-boarding. BG-recorded; EVA kerbal terminal kind = Crashed; kerbal dies.
4. Player returns to Space Center. Merge. Lander Immutable; EVA recording CommittedProvisional + Crashed. `KerbalAssignment`-to-Dead action emitted; paired rep penalty emitted within 1s → both are tombstone-eligible but not yet tombstoned (tombstoning happens on re-fly merge, not on the initial tree merge). EVA appears in Unfinished Flights.
5. Player invokes RP-2 targeting the EVA kerbal slot. The lander plays back as a ghost; the kerbal is live on the ladder.
6. Player carefully re-boards the lander. Recording terminates at the re-board event (which Parsek records as a merge boundary). TerminalKind=Landed (or Docked, depending on classifier interpretation — both map to Immutable).
7. Merge. Supersede relation `{EVA → EVA'}` appends. Kerbal-death action tombstoned; bundled rep penalty tombstoned. `CrewReservationManager.RecomputeAfterTombstones` re-derives; the dead kerbal returns to active. EVA' Immutable; both slots Immutable → RP-2 reaps.

### A.3 Docking merge + re-fly

1. Player launches a tanker to rendezvous with a station. Lander undocks from the station; both controllable → RP captured.
2. Player flies the tanker. Lander is BG-recorded. Due to an autopilot bug the lander de-orbits and crashes.
3. Merge. Lander CommittedProvisional Crashed → Unfinished Flight. Tanker Immutable.
4. Player rewinds. Lander is live; tanker plays back as a ghost; station's own tree is unrelated and stays physical (or ghost, depending on its own chain state).
5. Player manually flies the lander back up and docks with the station. Recording terminates at dock; `TerminalKind = Docked` → maps to Landed kind → Immutable merge.
6. Supersede `{Lander → Lander'}`. Station's tree unchanged. RP reaps if the tanker slot is also Immutable.

### A.4 Revert-during-re-fly (3-option dialog)

1. Player is in the middle of an A.1-style booster re-fly. Rocket barely controllable after re-entry.
2. Player hits Esc → Revert to Launch. `RevertInterceptor.Prefix` sees the active marker → spawns the 3-option dialog.
3. **Retry from Rewind Point**: dialog dismisses. `RetryHandler` clears the marker, generates a fresh session id, re-invokes `RewindInvoker.StartInvoke(rp, slot)`. Scene reloads back to split moment with a new provisional A''. The old A' is now a zombie for the next load-time sweep (or for the current-session load cycle's sweep if another save happens first).
4. **Discard Re-fly**: dialog dismisses. `DiscardReFlyHandler` removes the provisional A' from `CommittedRecordings`, promotes the origin RP to persistent (`SessionProvisional=false`, `CreatingSessionId=null`) so `LoadTimeSweep` does not reap it, clears the marker + journal, bumps the supersede state version, then stages the origin RP's quicksave through the save-root copy path and calls `GamePersistence.LoadGame` + `HighLogic.LoadScene(GameScenes.SPACECENTER)`. The player lands in the Space Center at the RP's UT. The Unfinished Flights entry is still present (the origin crash recording remains visible as an `Immutable` legacy crash or `CommittedProvisional` current crash, and its RP was promoted, so `IsUnfinishedFlight` continues to resolve true). Career state is unchanged. The tree's other committed supersede relations, tombstones, and Rewind Points are preserved — other re-fly attempts already merged into the tree are unaffected.
5. **Continue Flying**: dialog dismisses; nothing happens. Player keeps trying.

The same 3-option dialog appears when the player picks Revert-to-VAB (or Revert-to-SPH for a spaceplane). Retry's behaviour is identical (RP quicksave is FLIGHT-scoped — Retry lands back at the split moment in FLIGHT, not in the editor). Discard Re-fly in that case pre-sets `EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN` and `EditorDriver.editorFacility = facility` before `HighLogic.LoadScene(GameScenes.EDITOR)` so the scene swap lands in the VAB or SPH as originally clicked, with a fresh editor against the just-loaded career.

### A.5 Crash-quit-resume mid-re-fly

1. Player is 30 seconds into a re-fly. Home power fails. KSP dies without saving.
2. Player reopens KSP, loads the save. `ParsekScenario.OnLoad` loads the scenario including the `REFLY_SESSION_MARKER` that was on disk from the last F5 quicksave (if one was taken during the session) or from the last auto-save.
3. `MergeJournalOrchestrator.RunFinisher` runs first; no journal present (the player wasn't in the middle of merging), returns false.
4. `RewindInvoker.ConsumePostLoad` runs; no `RewindInvokeContext.Pending`, returns.
5. `LoadTimeSweep.Run` runs. `MarkerValidator.Validate` checks the six durable fields against the current scenario state. If the provisional recording from the session is still in `CommittedRecordings` with `MergeState == NotCommitted` AND the origin recording + RP + tree all resolve AND `InvokedUT` is in the past → marker valid. Spare set = {provisional, RP}. No discard.
6. `RewindPointReaper.ReapOrphanedRPs` runs. The RP is in the spare set and session-provisional; nothing to reap.
7. Player picks up where they left off. The live scene may need them to recover physics state (KSP's own auto-save quirks), but the Parsek-side re-fly session is intact.

Alternative: if the scenario state drifted (e.g. the provisional was corrupted on disk), `MarkerValidator` fails on the first mismatching field, emits `[ReFlySession] Warn: Marker invalid field=<f>; clearing`, clears the marker, and the nested-cleanup pass discards the provisional + session-provisional RPs. The player reverts to the pre-invocation state, with Unfinished Flights still showing the original retired sibling (because its CommittedProvisional stays). They can click Rewind again.

---

*End of Rewind to Separation design doc.*

