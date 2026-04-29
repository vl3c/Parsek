# Parsek — Unfinished Flights: Stable-Leaf Extension

*Forward-looking design specification for broadening v0.9's Unfinished Flights group to include stable-but-unconcluded leaves of multi-controllable splits — probes deployed and forgotten in orbit, stranded EVA kerbals, sub-orbital coast that never resolved — alongside today's Crashed-only siblings. This document is the contract for the implementation PRs; it does not describe shipped behavior yet.*

*Status: planned for a 0.9.x patch. Promoted from the research note `docs/dev/research/extending-rewind-to-stable-leaves.md` (R17, merged in PR #634). Related docs: `parsek-rewind-to-separation-design.md` (the v0.9 source of truth this feature extends), `parsek-recording-finalization-design.md` (terminal-state contract this feature relies on), `parsek-flight-recorder-design.md` (recording DAG, branch-points, chain semantics), `parsek-timeline-design.md` (read-only consumer of ERS).*

---

## 1. Introduction

### 1.1 Problem

Today's v0.9 Unfinished Flights group surfaces only `TerminalKind.Crashed` siblings of multi-controllable splits. The narrowing in v0.9 §7.31 ("stable-end splits explicitly not in scope") was correct for the rescue-from-destruction use case, but it leaves a class of flights that the player may want to come back to:

- A mothership deploys four science probes at Mun orbit, the player flies the mothership home; the four probes sit in their parking orbits forever, with no in-game path to fly any of them later.
- An EVA kerbal slips off the ladder and ends up stranded on a Mun surface (alive but un-reboarded); v0.9 surfaces this only if the kerbal *died*, not if they merely missed the lander.
- A multi-stage rocket leaves an upper stage in a sub-orbital arc that the player intended to circularize but never got around to.

The player's mental model is "this flight is unfinished — I want to come back to it." The v0.9 algorithm reads "the universe ended this safely — there's nothing to re-fly." Both readings are partially right; this feature broadens the predicate so the player's reading wins for the cases they actually care about, while keeping routine focus-continuation upper stages out of the list.

### 1.2 Scope

The v1 feature covers:

- **Broadened predicate.** `EffectiveState.IsUnfinishedFlight` is extended to include stable-terminal non-focus controllable leaves and stranded EVA kerbals. The structural-leaf gate, controllable-subject gate, and slot-still-open gate are all preserved or tightened.
- **One Unfinished Flights group, broader membership.** No new virtual group. The existing tooltip and group affordances stay; the predicate behind it changes.
- **Per-row Seal action.** A new in-table action that closes a slot permanently without touching the underlying recording, so the player can clear over-included rows and let the reaper free disk space.
- **New persistent fields.** `ChildSlot.Sealed` (+ `SealedRealTime`), `ChildSlot.Parked` (+ `ParkedRealTime`), `RewindPoint.FocusSlotIndex`, and `ReFlySessionMarker.SupersedeTargetId`. All back-compat.
- **Helper extraction.** `TryResolveRewindPointForRecording` and friends move from `UI/RecordingsTableUI.cs` into a new `UnfinishedFlightClassifier` static class so non-UI consumers (`RecordingStore`, `SupersedeCommit`) can call them without a layering inversion.
- **Closure-helper split.** `EffectiveState.ComputeSessionSuppressedSubtree(marker)` becomes a thin wrapper around a new `ComputeSubtreeClosureInternal(marker, rootOverride)` so merge-time supersede append can re-root the closure walk while runtime ghost suppression stays origin-rooted.

### 1.3 Out of scope (v1)

- ~~**Park-from-not-UF affordance.** A complementary UI action that adds a row the default predicate excluded (e.g. a Landed rover the player wants re-flyable later, a focus-continuation upper stage they decided to come back to). Deferred to v2 unless playtest shows demand.~~ Implemented in the v2 follow-up as `ChildSlot.Parked` while the RP still exists; already-reaped RP quicksaves are not resurrected.
- **Voluntary-action heuristics.** A1/A2/A4 orbit-shift / mid-chain-surface / body-change classifiers that R1-R3 explored. Explicitly rejected upstream of v1; over-inclusion is handled by the Seal button instead.
- **Migration sweep for legacy star-shaped supersede graphs.** §6.4 covers the topology change; the existing star portions in legacy saves are tolerated as-is.
- **Auto-purge of long-lived sealed RPs.** No TTL-based reaper extension. Disk usage is the player's responsibility via the Seal button, surfaced through the Settings → Diagnostics disk-usage line and its crashed/stable/sealed-pending RP breakdown.

### 1.4 Prerequisites

This feature depends on a separate v0.9 invocation-linearization PR landing first. See §11. The prerequisite PR fixes a v0.9 bug where re-fly invocation produces a star-shaped supersede graph that resolves incorrectly under chain extension; this feature's Site B-1 / Site B-2 design relies on the linear-graph behavior that PR establishes.

### 1.5 Who benefits

- Players running constellation-deploy missions (probes, satellites, ground stations) who want to come back and fly individual deployed objects later.
- Players whose EVA kerbal got stranded on a surface alive (suit ran out before reboard, fell off ladder during return) — v0.9 only handled the death case, this feature handles the alive-but-unreboarded case.
- Players who crash a vessel after a structural breakup but want to re-fly the breakup moment — partially covered (see §7 §S22 risk).

---

## 2. Design Philosophy

These principles govern every decision in this doc. They are listed up front because they inform every section that follows.

### 2.1 Correct visually, minimal, efficient

Borrowed from the project-wide recording-design principle. The classifier runs per-frame on every committed recording in ERS; the predicate is structured as cheap structural gates first, expensive terminal-state-and-focus checks last, with single-line shortcut returns. The closure helper is cached.

### 2.2 Append-only history, slot-level close signal

The recording tree never shrinks. The new `ChildSlot.Sealed` flag is the close signal — set once on player invocation, never cleared in-game. Sealed is decoupled from `MergeState`: a recording's MergeState reflects the merge journal's outcome; a slot's Sealed reflects the player's choice to close that slot permanently. They serve different purposes and v0.9's existing legacy-Immutable-crash UF rows continue to qualify because nobody will have Sealed them.

### 2.3 Narrow v1 semantics, Seal as the override

The classifier auto-includes obvious-feeling cases (Crashed, Orbiting non-focus, SubOrbital non-focus, EVA-stranded). Over-inclusion is handled by the player Sealing the row. Under-inclusion (rover drove 20m and player wants it re-flyable) was accepted as a v1 limitation; the v2 Park-from-not-UF affordance is the explicit escape hatch while the backing RP still exists. **No heuristic predicates** beyond the simple terminal-state-plus-focus rule. R1-R3's voluntary-action heuristic exploration was explicitly rejected.

### 2.4 Predicate must not drift between call sites

The predicate is shared between two call sites — Site A (original tree commit) and Site B-1 (re-fly merge classifier flip). Both route through the same `UnfinishedFlightClassifier.Qualifies(rec, slot, rp, considerSealed)` entry point. A drift would mean the predicate's verdict changes depending on which path produced the row, which the player would experience as "rows that disappear immediately after merge." A shared-classifier identity test guards against drift. Site B-2 is a separate concern — it consumes Site B-1's verdict and decides what to do with it on the in-place merge path; see §6.3.

### 2.5 Layering: classifier owns the helpers, UI consumes

`TryResolveRewindPointForRecording`, `IsUnfinishedFlightCandidateShape`, `IsVisibleUnfinishedFlight` move from `UI/RecordingsTableUI.cs` into the new `UnfinishedFlightClassifier`. `RecordingStore`, `SupersedeCommit`, and the Seal handler all call them from a non-UI namespace. UI continues to consume the classifier; the classifier never reaches into UI.

### 2.6 Forward-only for vessels, retroactive for stranded kerbals

Pre-feature saves with legacy live RPs whose Orbiting siblings are Immutable do NOT retroactively populate UF — the `FocusSlotIndex == -1` short-circuit suppresses Orbiting/SubOrbital qualification on legacy RPs (no focus signal to discriminate routine upper stages from probe deploys). Stranded EVA kerbals from legacy saves DO retroactively appear (the EVA branch returns before the focus short-circuit). Two CHANGELOG notes required, see §9.2.

### 2.7 Observable from logs alone

Every predicate gate emits a `[UnfinishedFlights] Verbose` line with a structured reason. The reaper logs sealed-slot-contributing counts. Site A and Site B-1 emit `[UnfinishedFlights] Info` on promotion with the resolved slot+RP. Seal accept/cancel logs. A KSP.log reader can reconstruct why every row appeared or didn't, and what the Seal action did to each slot. See §10.

---

## 3. Terminology

This section fixes the vocabulary. "Recording" (lowercase) is the concept; **Recording** (capitalized, code-font) is the class (`Source/Parsek/Recording.cs`).

- **UF** — Unfinished Flight. A recording that satisfies `IsUnfinishedFlight(rec)` and is therefore visible in the Unfinished Flights virtual group.
- **Stable terminal** — a terminal state in `{ Orbiting, SubOrbital, Landed, Splashed, Recovered, Docked, Boarded }`. The opposite is `Destroyed` (Crashed) and the absent terminal (no `TerminalStateValue` set).
- **Stable leaf** — a recording whose chain TIP has a stable terminal AND whose `ChildBranchPointId` is null OR equals the matched RP's `BranchPointId` (the breakup-survivor case).
- **Focus slot** — the `ChildSlot` in a `RewindPoint` whose vessel was the active focus at the moment the multi-controllable split fired. Identified by `RewindPoint.FocusSlotIndex` (default -1 = no focus signal).
- **Non-focus slot** — any slot other than the focus slot. `slot.SlotIndex != RP.FocusSlotIndex AND RP.FocusSlotIndex >= 0`.
- **No-focus-signal RP** — a RewindPoint with `FocusSlotIndex == -1`. Either a legacy RP that predates this feature, OR a new RP where no slot was focused at split time (rare: e.g. the player was focused on an unrelated vessel outside the split).
- **Sealed slot** — a `ChildSlot` with `Sealed == true`. The player has explicitly closed this slot; the row drops from UF and the reaper treats it as equivalent-to-Immutable for reap eligibility.
- **Stranded EVA** — an EVA kerbal recording whose chain TIP has `EvaCrewName != null` AND a non-`Boarded` terminal (typically `Landed` for surface strands, `Orbiting` for drift strands; `Destroyed` is a dead kerbal and routes through the Crashed branch).
- **Prior tip** — the slot's current effective recording id at re-fly invocation time, before any new supersede relation has been appended for the current re-fly. Equals `slot.OriginChildRecordingId` on the first re-fly into a slot; equals the previous re-fly's recording id on chain extension.
- **Site A** — `RecordingStore.ApplyRewindProvisionalMergeStates`. The MergeState-promotion call site that runs at original tree commit.
- **Site B-1** — `SupersedeCommit.FlipMergeStateAndClearTransient`. The MergeState-promotion call site that runs at re-fly merge.
- **Site B-2** — `MergeDialog.TryCommitReFlySupersede`'s in-place continuation path. After Site B-1 runs, this path currently overrides the classifier's verdict with an unconditional `provisional.MergeState = MergeState.Immutable`. The override exists because the in-place path has no separate provisional (the same recording is both slot effective AND supersede target), and leaving it CP would create a duplicate / un-reapable UF row. Site B-2's job in this feature is to handle the in-place CP case correctly per §6.3 — three candidate options (B2-A force-Immutable / B2-B fresh-provisional / B2-C auto-Seal), decision deferred to §11.3.

---

## 4. Mental Model

### 4.1 The 4-probe canonical example

The motivating gameplay:

```
Mun orbit, mothership M with 4 attached probes P1-P4.
Player triggers staging; all 4 decouplers fire simultaneously.
Multi-controllable split: M + P1 + P2 + P3 + P4 = 5 controllables.
RewindPointAuthor.Begin captures RP-1 with 5 ChildSlots.
RP-1.FocusSlotIndex = (slot index for M)  // the player's active vessel.

Player flies M back to Kerbin. P1-P4 background-record their parking coast.
Tree commits.
```

Today (v0.9): all 5 recordings commit `Immutable` (none crashed). `RewindPointReaper.IsReapEligible` sees all-slots-Immutable → reap RP-1. Quicksave deleted, BranchPoint.RewindPointId cleared. P1-P4 are inert rows in the Recordings table forever.

After this feature:

```
Site A (ApplyRewindProvisionalMergeStates) walks tree.Recordings:
  M:   focus slot, terminal Orbiting (Kerbin) -> not UF -> Immutable
  P1:  non-focus, terminal Orbiting (Mun)     -> UF        -> CommittedProvisional
  P2:  non-focus, terminal Orbiting (Mun)     -> UF        -> CommittedProvisional
  P3:  non-focus, terminal Orbiting (Mun)     -> UF        -> CommittedProvisional
  P4:  non-focus, terminal Orbiting (Mun)     -> UF        -> CommittedProvisional

RewindPointReaper.IsReapEligible: 4 slots are CP (open) -> RP-1 stays alive.
Recordings Manager Unfinished Flights group shows P1, P2, P3, P4 with Fly + Seal buttons.
```

Player can pick any probe individually, hit Fly, rewind to the staging UT, and fly that probe to a real mission (land it, transfer it elsewhere, etc.). The other three probes ghost-play-back from their committed coast. Merge produces a re-fly that supersedes the original probe; if the re-fly ends Landed/Splashed/Recovered, the slot closes Immutable. If it ends Orbiting in a different orbit, the slot stays CP for further re-fly. If the player decides one probe is "done," they hit Seal and the row drops.

### 4.2 Focus exclusion: routine upper stages don't pollute the list

```
Two-stage rocket: booster B (probe core, parachutes) + upper stage U (player's mission).
Player flies U to orbit. B BG-records, parachutes auto-deploy, B lands safely.
Tree commits.

RP-1.FocusSlotIndex = (slot index for U)
B:  non-focus, terminal Landed   -> not UF (Landed is "stable conclusion")
U:  focus, terminal Orbiting     -> not UF (focus exclusion)

All slots Immutable -> RP reaps. Same as v0.9 behavior.
```

Inversely (the "spaceplane that recovers a strap-on booster" case from §S19b in the research note):

```
Player flies B (the booster) to recover it. U (upper stage) BG-records, ends Orbiting.

RP-1.FocusSlotIndex = (slot index for B)
B:  focus, terminal Landed       -> not UF (focus exclusion + Landed)
U:  non-focus, terminal Orbiting -> UF -> CommittedProvisional
```

The booster is excluded as the focus mission; the upper stage is the unfinished off-mission sibling. RP stays alive while U's slot is open.

### 4.3 EVA carve-out: stranded kerbals always qualify

The kerbal branch in `TerminalOutcomeQualifies` returns BEFORE the focus short-circuit. A stranded EVA kerbal qualifies as UF whether the player flew the EVA actively or not, and whether the RP is post-feature (with FocusSlotIndex set) or legacy (FocusSlotIndex == -1). Stranded kerbals are unambiguous (player wants them back); orbital siblings are ambiguous (intent unclear). This asymmetry is the only retroactive-surfacing exception in the migration story.

### 4.4 Seal closes a slot, not a recording

The player Seals a slot via the per-row Seal button. The seal flips `slot.Sealed = true` on the matching `ChildSlot`. It does NOT touch `Recording.MergeState`. The recording continues to play back as a ghost on any future rewind, exactly as before; only the re-fly opportunity for that slot is closed.

The reaper (§6.5) treats `slot.Sealed == true` as equivalent-to-Immutable for close-eligibility, BUT requires the effective recording to be in a committed MergeState (Immutable or CommittedProvisional). NotCommitted is unconditional no-reap regardless of Sealed — defends against load-time race states where a journal finisher rolled MergeState back to NotCommitted while the slot's Sealed bit is still on disk.

---

## 5. Data Model

New persistent fields. All back-compat: legacy ConfigNodes load with safe defaults.

### 5.1 ChildSlot.Sealed / SealedRealTime + Parked / ParkedRealTime

File: `Source/Parsek/ChildSlot.cs`.

```csharp
public class ChildSlot
{
    // ... existing fields (SlotIndex, OriginChildRecordingId, Controllable, Disabled, DisabledReason)

    /// <summary>
    /// True if the player invoked the per-row Seal action on this slot,
    /// closing it permanently. Excluded from IsUnfinishedFlight; treated as
    /// equivalent-to-Immutable by RewindPointReaper for close-eligibility,
    /// BUT NotCommitted effective recordings always block reap regardless
    /// of this flag (defends against load-time race states).
    ///
    /// Default false; legacy saves load with false (existing crash UF rows
    /// continue to qualify). Player has no in-game un-seal path; a tree-
    /// scoped Full-Revert is the only undo.
    /// </summary>
    public bool Sealed;

    /// <summary>
    /// Wall-clock ISO-8601 UTC timestamp the Seal was applied. Diagnostic
    /// only; null when Sealed is false.
    /// </summary>
    public string SealedRealTime;

    /// <summary>
    /// True when the player invoked the per-row Park action on this slot,
    /// promoting a default-excluded stable terminal leaf into Unfinished
    /// Flights. Excluded only when the slot is Sealed or the structural
    /// classifier closes it (for example boarded EVA / downstream BP).
    ///
    /// Default false; legacy saves load with false. Park is only possible
    /// while the backing RewindPoint still exists; it does not resurrect
    /// already-reaped RP quicksaves.
    /// </summary>
    public bool Parked;

    /// <summary>
    /// Wall-clock ISO-8601 UTC timestamp the Park was applied. Diagnostic
    /// only; null when Parked is false.
    /// </summary>
    public string ParkedRealTime;
}
```

ConfigNode keys (extend `SaveInto`/`LoadFrom`):

```
CHILD_SLOT
{
    slotIndex = 0
    originChildRecordingId = rec_...
    controllable = True
    disabled = True              # omitted when False
    disabledReason = ...         # omitted when null
    sealed = True                # omitted when False (NEW)
    sealedRealTime = 2026-...    # omitted when null (NEW)
    parked = True                # omitted when False (NEW)
    parkedRealTime = 2026-...    # omitted when null (NEW)
}
```

### 5.2 RewindPoint.FocusSlotIndex

File: `Source/Parsek/RewindPoint.cs`.

```csharp
public class RewindPoint
{
    // ... existing fields

    /// <summary>
    /// Slot index (0-based, into ChildSlots) of the vessel that was the
    /// active focus at split time. -1 means "no focus signal available":
    /// either a legacy RP that predates this field, OR a new RP where no
    /// slot was focused at split time (e.g. background joint break, or a
    /// split where the player was focused on an unrelated vessel outside
    /// the split).
    ///
    /// Used by IsUnfinishedFlight's TerminalOutcomeQualifies to gate
    /// stable-terminal qualification: focus-continuation slots are excluded
    /// from auto-UF for Orbiting / SubOrbital. Crashed and EVA-stranded
    /// terminals qualify regardless of focus.
    ///
    /// FocusSlotIndex == -1 short-circuits Orbiting/SubOrbital to false
    /// (the conservative choice when no focus signal is available),
    /// preserving v0.9 Crashed-only behavior for legacy RPs and avoiding
    /// retroactive flooding from background-only splits.
    /// </summary>
    public int FocusSlotIndex = -1;
}
```

ConfigNode key: `focusSlotIndex`, written when != -1.

```
POINT
{
    rewindPointId = rp_...
    branchPointId = bp_...
    ... existing fields ...
    focusSlotIndex = 2           # omitted when -1 (NEW)
    CHILD_SLOT { ... }
    PID_SLOT_MAP { ... }
    ROOT_PART_PID_MAP { ... }
}
```

### 5.3 ReFlySessionMarker.SupersedeTargetId

File: `Source/Parsek/ReFlySessionMarker.cs`.

```csharp
public class ReFlySessionMarker
{
    // ... existing fields (SessionId, TreeId, ActiveReFlyRecordingId,
    //                     OriginChildRecordingId, RewindPointId, InvokedUT,
    //                     InvokedRealTime)

    /// <summary>
    /// The supersede target at invocation time -- the slot's CURRENT EFFECTIVE
    /// recording (slot.EffectiveRecordingId(supersedes)). For markers written
    /// by post-feature invocations, this field is ALWAYS set, even on the
    /// first re-fly into a slot (where the value equals OriginChildRecordingId).
    /// Legacy markers (written before this field existed) load with null;
    /// AppendRelations coalesces null and "equal to OriginChildRecordingId"
    /// behaviour by falling back to OriginChildRecordingId when null.
    ///
    /// Used by SupersedeCommit.AppendRelations as the root of the subtree
    /// closure walk: closure rooted at SupersedeTargetId (= prior tip on
    /// chain extension; = slot origin on first re-fly). CommitTombstones
    /// receives the same closure for tombstone scoping.
    ///
    /// This decouples the supersede-graph topology from the slot identity
    /// (held in OriginChildRecordingId), so existing consumers that key off
    /// the slot's immutable origin (RevertInterceptor.FindSlotForMarker,
    /// in-place continuation, ghost suppression) continue to work unchanged.
    /// </summary>
    public string SupersedeTargetId;
}
```

ConfigNode key: `supersedeTargetId`, **always written** when the marker is persisted post-feature. Legacy markers load with null (key absent on disk).

`MarkerValidator.Validate` extends to weakly validate `SupersedeTargetId`: when non-null, must resolve in `RecordingStore.CommittedRecordings` or the matching pending tree; failure logs `[ReFlySession] Warn: Marker invalid field=SupersedeTargetId; clearing` and clears the field. Null is always valid.

### 5.4 No new ConfigNode wrapper sections

This feature does not add any top-level sections under `ParsekScenario`. All three new fields are properties on existing persistent objects (`ChildSlot`, `RewindPoint`, `ReFlySessionMarker`).

---

## 6. Behavior

### 6.1 The predicate

`EffectiveState.IsUnfinishedFlight(Recording rec)` is rewritten to use the shared classifier. The full evaluation:

```
IsUnfinishedFlight(rec) :=
    // Filter 1: visible, committed recording
    rec is in ERS                                                  // §1
    AND rec.MergeState in { Immutable, CommittedProvisional }       // §2

    // Filter 2: controllable subject at chain HEAD
    AND chainHead.IsDebris == false                                 // §3

    // Filter 3: matching RP with an open slot, with per-RP-context leaf check
    AND exists RP, exists slot in RP.ChildSlots such that:
        // Slot resolution -- v0.9 logic, unchanged shape
        slot.EffectiveRecordingId(supersedes) == rec.RecordingId
        AND (rec.ParentBranchPointId == RP.BranchPointId
             OR rec.ChildBranchPointId == RP.BranchPointId)
                                                                    // §4
        // Slot-close gate
        AND slot.Sealed == false                                    // §5

        // Per-RP leaf gate
        AND let chainTip = ResolveChainTerminalRecording(rec)
            (chainTip.ChildBranchPointId == null
             OR chainTip.ChildBranchPointId == RP.BranchPointId)    // §6

        // Outcome gate
        AND TerminalOutcomeQualifies(chainTip, slot, RP)            // §7

TerminalOutcomeQualifies(chainTip, slot, RP) :=
    let kerbal   = !string.IsNullOrEmpty(chainTip.EvaCrewName)
    let terminal = chainTip.TerminalStateValue
    let isFocus  = (slot.SlotIndex == RP.FocusSlotIndex)
    let noFocusSignal = (RP.FocusSlotIndex == -1)

    if !terminal.HasValue:
        return false                          // no terminal recorded

    // Crashed always qualifies, regardless of kerbal/focus/everything.
    // This branch runs FIRST so a dead EVA kerbal (EvaCrewName != null
    // AND terminal == Destroyed) routes here -- not through the kerbal
    // branch below -- so the reason logging says reason=crashed and the
    // §7.8 narrative matches the code.
    if terminal.Value == Destroyed:
        return true                           // Crashed

    if kerbal:
        // At this point terminal is guaranteed not Destroyed (handled above).
        // Any non-Boarded stable terminal is a stranded EVA: surface
        // (Landed/Splashed), drift (Orbiting/SubOrbital), or Docked
        // edge cases. Returns true. Boarded would mean the kerbal was
        // reboarded -- but the BoardBP makes the recording non-leaf via
        // the structural gate before we ever reach here, so this branch's
        // "!= Boarded" is effectively redundant. Listed for completeness.
        //
        // EVA branch returns BEFORE the noFocusSignal short-circuit:
        // stranded kerbals surface even from legacy / no-focus-signal RPs.
        // Intentional retroactive carve-out (see §9.1).
        return terminal.Value != Boarded

    // Stable in-flight terminals: only non-focus slots on RPs with a
    // defined focus signal qualify. noFocusSignal short-circuits to false.
    if noFocusSignal:
        return false

    if terminal.Value == Orbiting   && !isFocus: return true
    if terminal.Value == SubOrbital && !isFocus: return true
        // Vacuum-arc SubOrbital. Atmospheric SubOrbital is reclassified
        // to Destroyed by BallisticExtrapolator before commit (see
        // BallisticExtrapolator.cs SubSurfaceStart short-circuit +
        // IncompleteBallisticSceneExitFinalizer terminal stamping).

    // Landed / Splashed / Recovered / Docked: stable surface or recovered
    // terminal. The universe gave the vessel a stable conclusion. Default
    // does not include the row regardless of focus.
    return false
```

The implementation lives in a new `UnfinishedFlightClassifier` static class (§6.2). The signature matches the v0.9 `IsUnfinishedFlight(Recording rec)` for backwards-compatible callers, but the implementation routes through `UnfinishedFlightClassifier.Qualifies(rec, slot, rp, considerSealed: true)` after the slot+RP resolution.

### 6.2 Classifier extraction

A new file `Source/Parsek/UnfinishedFlightClassifier.cs` (or alternatively all-in-one in `EffectiveState.cs`) owns:

- `Qualifies(Recording rec, ChildSlot slot, RewindPoint rp, bool considerSealed)` — the predicate body.
- `TerminalOutcomeQualifies(Recording chainTip, ChildSlot slot, RewindPoint rp)` — the outcome gate.

The following helpers move from `UI/RecordingsTableUI.cs` into this file (or `EffectiveState.cs`):

- `TryResolveRewindPointForRecording(Recording rec, out RewindPoint rp, out int slotListIndex)`
- `IsUnfinishedFlightCandidateShape(Recording rec)`
- `IsVisibleUnfinishedFlight(Recording rec, out string reason)`

`RecordingsTableUI` becomes a consumer of the moved helpers. Other consumers (Site A and Site B-1 predicate-call paths in §6.3, plus the Seal handler in §6.6) call them from non-UI namespaces. Site B-2 reads Site B-1's verdict downstream and does not call the predicate directly; it lives in `MergeDialog` and consumes the marker + the just-classified provisional state.

### 6.3 MergeState promotion: Site A + Site B-1 (predicate-evaluation), Site B-2 (in-place handling)

The predicate must be applied at two call sites — Site A and Site B-1. Both route through `UnfinishedFlightClassifier.Qualifies` to prevent drift. Site B-2 is a separate concern that consumes Site B-1's verdict on the in-place merge path; it does not evaluate the predicate independently.

**Site A: original tree commit.** `RecordingStore.ApplyRewindProvisionalMergeStates` ([RecordingStore.cs:715-770](../Source/Parsek/RecordingStore.cs)) extends from the v0.9 Crashed-only check to the broader predicate:

```
for each rec in tree.Recordings:
    if rec.MergeState != Immutable: continue
    if rec.chainHead.IsDebris: continue

    if NOT TryResolveRewindPointForRecording(rec, out rp, out slotIdx):
        continue
    var slot = rp.ChildSlots[slotIdx]

    // considerSealed=false: a freshly committed slot is never Sealed yet.
    if NOT UnfinishedFlightClassifier.Qualifies(
            rec, slot, rp, considerSealed: false):
        continue

    rec.MergeState = CommittedProvisional
    log [UnfinishedFlights] Info: CommitTree promoted rec=<rid>
        slot=<slotIdx> rp=<rpId> reason=<crashed|stableLeafUnconcluded|strandedEva>
```

The existing v0.9 crash-only path is subsumed: `Qualifies` returns true for `terminal == Destroyed` regardless of focus.

**Site B-1: re-fly merge classifier flip.** `SupersedeCommit.FlipMergeStateAndClearTransient` extends from `(kind == Crashed) ? CP : Immutable` to:

```
// Slot resolution: walk supersedes from each slot's OriginChildRecordingId
// forward; the helper returns the slot whose forward trail contains the
// queried provisional. After Phase 2 (AppendRelations) of the merge journal,
// the new {priorTip -> provisional} relation is in supersedes (per §6.4),
// so the walk reaches the provisional cleanly.
if NOT TryResolveRewindPointForRecording(provisional, out rp, out slotIdx):
    // Hard failure in DEBUG (Debug.Assert + log Error); fall back to the
    // v0.9 default in RELEASE for crash safety. A release-build occurrence
    // indicates a §11 prerequisite regression or AppendRelations bug --
    // not a recoverable runtime state.
    log [Supersede] Error: Site B-1 slot lookup failed for provisional=<rid>
        rpId=<marker.RewindPointId> originChildRec=<marker.OriginChildRecordingId>
        supersedeTargetId=<marker.SupersedeTargetId>
    Debug.Assert(false, "Site B-1 slot lookup failed")
    provisional.MergeState = (Classify(provisional) == Crashed)
                                ? CommittedProvisional : Immutable
    return

var slot = rp.ChildSlots[slotIdx]
bool qualifies = UnfinishedFlightClassifier.Qualifies(
    provisional, slot, rp, considerSealed: false)
provisional.MergeState = qualifies ? CommittedProvisional : Immutable
log [Supersede] Info: provisional=<rid> mergeState=<state> qualifies=<b>
    slot=<slotIdx> rp=<rpId> focusSlot=<rp.FocusSlotIndex>
```

**Site B-2: in-place continuation merge handling.** `MergeDialog.TryCommitReFlySupersede` currently calls `FlipMergeStateAndClearTransient` and then immediately overrides with `provisional.MergeState = MergeState.Immutable` regardless of the classifier verdict. The override exists for a real invariant: on the in-place path there is **no separate provisional**. The same `Recording` instance plays both roles — it IS the slot's effective recording AND the supersede target. If we let it stay `CommittedProvisional` after merge, the row never drops from Unfinished Flights (the recording is its own effective; the slot stays CP; the predicate keeps qualifying it), creating a duplicate / un-reapable state.

Removing the override naively therefore introduces a regression. Site B-2 must produce one of these end states for in-place re-flies that Site B-1 would otherwise classify as CP (Orbiting/SubOrbital/EVA-stranded non-focus):

- **(B2-A) Force Immutable** — current v0.9 behavior preserved. The slot closes; chain extension via the in-place path on stable terminals is unavailable. The player must use a separate RP (a different split) to re-fly again. Simplest; lowest implementation risk; matches the v0.9 invariant exactly.
- **(B2-B) Switch the in-place path to use a fresh provisional** — architectural change. The supersede chain has distinct old/new endpoints; chain extension works the same as the fresh-provisional path. Cleanest semantically; non-trivial code change to the in-place merge code path.
- **(B2-C) Auto-Seal the slot on in-place CP merge** — set `slot.Sealed = true` whenever Site B-1 returned CP for an in-place merge whose recording is its own effective. Row drops post-merge; chain extension blocked. Hack-feeling because Seal is a player-explicit action elsewhere.

**Decision deferred to the §11.3 investigation, with a preference order.** The implementation PR picks one of (B2-A) / (B2-B) / (B2-C) only after the §11.3 code-archaeology pass establishes (a) the actual end state v0.9 produces, (b) what code paths consume that state downstream, and (c) which option is safest. Preference order:

1. **Try (B2-B) fresh-provisional first.** Cleanest gameplay: the design's promise of "stable unfinished re-flights chain naturally" holds for both in-place and fresh-provisional merge paths. A player who in-place-merges twice doesn't hit arbitrary friction. Ship if §11.3 confirms the in-place path can be re-architected to spawn a fresh provisional within v1 scope.
2. **Fall back to (B2-A) force-Immutable** if B2-B requires a deep refactor of `MergeDialog.TryCommitReFlySupersede` that's out of scope for v1. Preserves v0.9 behavior exactly; documented chain-extension limitation in CHANGELOG.
3. **Avoid (B2-C) auto-Seal** unless both B2-B and B2-A are blocked. The Seal action is meant as an explicit player intent ("I'm done with this slot"); auto-firing it from a merge code path muddies the semantic and makes the per-row Seal log line ambiguous (was the seal a player action or a system action?).

The §10 risk + §13 test-plan entries are written against the (B2-A) fallback so the design ships under the conservative assumption; if §11.3 confirms B2-B is feasible, the test plan extends.

The over-arching shared-classifier rule still holds: Site A and Site B-1 always route through `UnfinishedFlightClassifier.Qualifies` for the predicate verdict. Site B-2 is about what to DO with that verdict on the in-place path, not whether to compute it.

### 6.4 Closure-helper split + invocation linearization (PREREQUISITE)

This subsection describes the v0.9 invocation change required for chain extension to work correctly. **The change ships in a separate PR before this feature** (see §11 Prerequisites). The text here documents the required end state.

**Background.** v0.9 invocation stamps `marker.OriginChildRecordingId` and `provisional.SupersedeTargetId` from `selected.OriginChildRecordingId` (the slot's immutable origin). `SupersedeCommit.AppendRelations` then writes `{slot.OriginChildRecordingId -> provisional}` for every re-fly. Multiple re-flies into the same slot produce a star-shaped graph rooted at the slot's origin. The walker `EffectiveState.EffectiveRecordingId` scans supersedes from the beginning and stops at the first match — on a star, it resolves to the oldest re-fly, missing later ones. Site B-1's slot lookup against the provisional then fails.

**Fix: linear semantics + closure-helper split.**

Marker-write change in `RewindInvoker.AtomicMarkerWrite`:

```
// Compute prior tip ONCE before the in-place vs fresh-provisional branch.
string priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes)

// Stamp BOTH marker fields unconditionally in the shared marker-creation
// block (runs on both branches).
marker.OriginChildRecordingId = selected.OriginChildRecordingId   // unchanged
marker.SupersedeTargetId      = priorTip                          // NEW

// Guard the provisional overwrite to the fresh-provisional branch only.
// In-place branch sets provisional == null.
if (provisional != null)
    provisional.SupersedeTargetId = priorTip                      // overwrite the
                                                                  // BuildProvisionalRecording
                                                                  // value
```

Closure-helper split in `EffectiveState`:

```csharp
// New: takes an explicit root, parameterized over the existing closure
// algorithm (PID-peer expansion via marker.InvokedUT, mixed-parent halt,
// chain-sibling expansion, all preserved). Cache key includes rootOverride.
internal static IReadOnlyCollection<string> ComputeSubtreeClosureInternal(
    ReFlySessionMarker marker, string rootOverride)
{
    // ... existing closure body, with rootOverride substituted for
    // marker.OriginChildRecordingId at the seed point.
}

// Existing public helper: thin wrapper preserving null-guard +
// cached-null-guard + defensive-copy contracts.
public static IReadOnlyCollection<string> ComputeSessionSuppressedSubtree(
    ReFlySessionMarker marker)
{
    if (marker == null)
        return Array.Empty<string>();
    var cached = ComputeSubtreeClosureInternal(
                     marker, marker.OriginChildRecordingId);
    if (cached == null)
        return Array.Empty<string>();
    return new HashSet<string>(cached, StringComparer.Ordinal);
}
```

`SupersedeCommit.AppendRelations` switches to call:

```
ComputeSubtreeClosureInternal(marker,
    marker.SupersedeTargetId ?? marker.OriginChildRecordingId)
```

`CommitTombstones` continues to receive the closure unchanged; tombstone scope correctly tracks the chain-extension's actual descendants.

Runtime ghost suppression (ghost playback engine, chain walker, ghost map presence, watch mode) continues calling `ComputeSessionSuppressedSubtree(marker)` and gets the same return value as today.

**Migration concern.** Existing saves with star-shaped supersede graphs from prior v0.9 Crashed chain extensions are tolerated as-is. The walker continues to pick the oldest re-fly in the star portion, then walks linearly from there. New chains extend linearly from whichever leaf the walker reached. The hybrid case (legacy star + new linear extension) is covered by §8.

### 6.5 Reaper rule

`RewindPointReaper.IsReapEligible` extends one term:

```
SlotIsClosed(slot, effectiveRecording) :=
    effectiveRecording.MergeState != NotCommitted        // unconditional no-reap
    AND
    (effectiveRecording.MergeState == Immutable          // existing v0.9 close signal
     OR slot.Sealed == true)                             // new R5 close signal

RP is reap-eligible iff every ChildSlot satisfies SlotIsClosed.
```

NotCommitted is unconditional no-reap regardless of `slot.Sealed`. This defends against load-time race states where the journal finisher rolled MergeState back to NotCommitted while the slot's Sealed bit is still on disk; reaping in that state would delete the RP quicksave while an active re-fly is still live.

Logging on reap:

```
[Rewind] Info: ReapOrphanedRPs: reaped=<R> remaining=<rem>
    sealedSlotsContributing=<S>
```

The new `sealedSlotsContributing` counter logs how many of the closed slots reached closure via the Seal path vs the Immutable path. Useful for understanding player behavior during playtest.

### 6.6 The Seal handler

The Seal action lives on each Unfinished Flight row in the Recordings Manager. Visual layout per §6.7.

Handler (in a new `UnfinishedFlightSealHandler` static class or as a method on the classifier):

```
1. Spawn Seal confirmation dialog (PopupDialog.SpawnPopupDialog with
   MultiOptionDialog body, see §6.7.2). Take input lock
   ParsekUFSealDialog.

2. On Cancel: log [UnfinishedFlights] Info: Seal cancelled rec=<rid>;
   release input lock; dismiss.

3. On Accept:
   - Locate slot via TryResolveRewindPointForRecording(rec, out rp, out slotIdx).
     If lookup fails: log [UnfinishedFlights] Error: Seal could not resolve
     slot for rec=<rid>; release input lock; dismiss; show toast
     "Seal failed -- slot not found." This should not happen for a row
     that was rendered as UF.
   - var slot = rp.ChildSlots[slotIdx]
   - slot.Sealed = true
   - slot.SealedRealTime = DateTime.UtcNow.ToString("o")
   - Bump SupersedeStateVersion (so ERS / UF group cache invalidates).
   - Determine reaperImpact: walk all slots of rp, check SlotIsClosed for
     each; "willReap" if all closed, "stillBlocked" otherwise.
   - log [UnfinishedFlights] Info: Sealed slot=<slotIdx> rec=<rid>
       bp=<bpId> rp=<rpId> terminal=<state> reaperImpact=<willReap|stillBlocked>
   - Release input lock; dismiss; row drops from group on next frame
     because the predicate now sees slot.Sealed == true.
```

**Seal does NOT touch `Recording.MergeState`.** Decoupling is load-bearing — see §2.2.

### 6.7 UI

#### 6.7.1 Layout

The Rewind column splits into Fly + Seal via **L1: widen the Rewind column**. `ColW_Rewind` increases from 75 px to ~150 px. Two `DrawBodyCenteredButton` calls side-by-side at ~70 px each. Header relabels to "Rewind / Seal."

L1 is chosen over L2 (new column), L3 (kebab menu), L4 (right-click) because:
- L1 stays inside the existing Rewind cell affordance pattern and adds zero new layout primitives. Crashed UF rows (today's v0.9 case) get the same layout — Fly is the primary action, Seal is the secondary. Seal-on-Crashed is "I accept the crash; stop offering me the re-fly."
- L2 adds an empty column for non-UF rows (most rows in the table), wasting horizontal space.
- L3 (kebab) is invisible discoverability — players don't know to click "...". KSP has no stock kebab pattern for tables.
- L4 (right-click) has no precedent in KSP's stock UI for table rows; adds an event handler + accessibility concern.

Cascade: every row in the Recordings Manager table gets the wider Rewind column. Crashed rows currently using R / FF / Fly / blank in the cell continue to render in the wider cell (the buttons are centered). Tooltip refresh on the Unfinished Flights group header.

#### 6.7.2 Seal confirmation dialog

`PopupDialog.SpawnPopupDialog` with a `MultiOptionDialog`. Title: "Seal Unfinished Flight?" Body:

```
Seal "<vessel-name>" (<terminal-state> at UT <ut>)?

This action CANNOT BE UNDONE.

After sealing:
  - This slot is closed permanently -- the recording can never be re-flown.
  - The "Fly" button on this row disappears.
  - The rewind point's quicksave file may be deleted (when every
    sibling of this split is also sealed or already finalized).
  - The recording itself is unchanged. It remains in your timeline
    and continues to play back as a ghost on any future rewind,
    exactly as it does now. Sealing only closes the re-fly slot;
    it does not erase the recording or its trajectory.

If you might want to re-fly this later, click Cancel.
```

Buttons: `Seal Permanently` (destructive style, fires the seal handler), `Cancel`. Input lock `ParsekUFSealDialog`.

### 6.8 Unfinished Flights group tooltip refresh

`UI/UnfinishedFlightsGroup.cs` tooltip changes from the v0.9 wording to:

> Vessels and kerbals that ended up in a state where you might want to re-fly them — crashed, abandoned in orbit, stranded on a surface. Click Fly to take control at the separation moment; click Seal to close the slot permanently if you're done with it.

---

## 7. Edge Cases

### 7.1 Single-controllable split
No RP. No slot. Predicate fails on the matching-RP filter. Not UF. ✓

### 7.2 Three or more controllable children at one split
One RP with N ChildSlots. `FocusSlotIndex` points at the focus slot (typically the active vessel's continuation). Each non-focus slot is independently considered by the predicate. **Planned test: extend `ApplyRewindProvisionalMergeStatesTests` + new `UnfinishedFlightClassifierTests`.**

### 7.3 4-probe deploy from a Mun mothership (the canonical case)
Mothership is focus → Immutable. 4 probes are non-focus, terminal Orbiting → 4 CP slots → 4 UF rows. RP stays alive while any probe slot is unsealed. **Planned in-game test.**

### 7.4 Auto-parachute booster, focus on upper stage
Booster non-focus, terminal Landed → not UF (Landed always returns false from `TerminalOutcomeQualifies`) → Immutable. Upper stage is focus, terminal Orbiting → not UF (focus exclusion) → Immutable. RP reaps cleanly. **Critical regression guard test.**

### 7.5 Inverted: focus on booster, upper stage left orbiting
Booster is focus, terminal Landed → not UF → Immutable. Upper stage non-focus, terminal Orbiting → UF → CP. RP stays alive while upper-stage slot is unsealed.

### 7.6 Stranded EVA kerbal (alive)
EVA kerbal terminal Landed (or Splashed for water) on a body, `EvaCrewName` non-null. Kerbal branch returns true regardless of focus. UF. Player can Fly to attempt reboard.

### 7.7 EVA kerbal reboards
Board BP fires. EVA recording's chain TIP `ChildBranchPointId` = boardBp.Id. Per-RP leaf gate: matchingRP.BranchPointId is the original EVA BP, not the Board BP. ChildBranchPointId != null AND != matchingRP.BranchPointId → leaf gate fails. Not UF. ✓

### 7.8 Dead EVA kerbal
Terminal `Destroyed`. Routes through the Crashed branch (Destroyed always returns true regardless of focus or kerbal status). UF. Same as v0.9 behavior.

### 7.9 Breakup-survivor active parent, terminal Crashed
Survivor V's chain TIP terminal Destroyed. Crashed branch returns true regardless of focus. UF. v0.9 behavior preserved exactly.

### 7.10 Breakup-survivor active parent, terminal Landed
Survivor V's chain TIP terminal Landed. Landed always returns false from `TerminalOutcomeQualifies`. Not UF. RP reaps when other slots close. v0.9 behavior.

### 7.11 Breakup-survivor active parent, terminal Orbiting (ACCEPTED LIMITATION)
Survivor V is FocusSlot, terminal Orbiting → not UF (focus exclusion). Player loses access to re-fly the breakup moment via UF by default. **Accepted v1 limitation — see §10 risk.** Mitigation: player can manually crash the post-breakup vessel and the row will appear, or use v2 Park while the backing RP still exists.

### 7.12 Cross-tree dock during stable-leaf re-fly
Re-flown probe docks with another tree's station. Dock BP fires; probe-re-fly's chain TIP gets `ChildBranchPointId = dockBp.Id`. Per-RP leaf gate fails. Site B-1 sees Docked terminal → `TerminalOutcomeQualifies` returns false → Immutable. Slot closes. AppendRelations closure walk (rooted at SupersedeTargetId) is tree-scoped and halts at the mixed-parent BP; station's tree unaffected. ✓

### 7.13 Re-fly a parked probe, end in Mun orbit (chain extension on stable terminal)
Re-fly merge: Site B-1 sees Orbiting + non-focus → CP. Slot stays open. Supersede chain extends linearly: probeOrig -> probeReFly1 (via §6.4 prerequisite). Player can Fly probe again; chain extends to probeReFly2 on a third re-fly. Player Seals to close.

### 7.14 Re-fly an auto-included stable-leaf probe, end Landed
Re-fly merge: Site B-1 sees Landed → not UF → Immutable. Slot closes. Supersede relation `{priorTip -> provisional}` appended. Reaper sees all slots closed → reaps RP. Manual-Park variant: if the player explicitly set `slot.Parked`, Landed remains `parkedStableLeaf` and the slot stays open until the player Seals it; boarded EVA and downstream-BP close-outs still use the normal closed path.

### 7.15 Re-fly a parked stranded EVA kerbal, succeed in reboarding
Re-fly merge produces a Board BP. Provisional has `ChildBranchPointId = boardBp.Id`. `TerminalOutcomeQualifies` returns false (Boarded → kerbal branch returns false). Site B-1 → Immutable. Slot closes. Stranded-kerbal-recovery path complete.

### 7.16 Player Seals a UF row
`slot.Sealed = true`; row drops from group; reaper runs (RP deleted if all sibling slots also closed, NotCommitted-not-allowed). Recording unchanged; ghost playback unchanged on subsequent rewinds. **Planned test.**

### 7.17 Player Seals a Crashed row (not a stable leaf)
Same Seal handler. The Crashed terminal originally qualified the row; Sealing closes it as "I accept the crash as canonical." Provides v0.9 users with a cleanup affordance they didn't have before.

### 7.18 Sealed row was never Immutable
A CP slot whose recording is in chain extension (e.g. probeReFly1 with subsequent CP attempts). Player Seals. `slot.Sealed = true`; reaper sees CP+Sealed → equivalent-to-Immutable for close-eligibility. RP reaps when other slots close.

### 7.19 Sealed slot whose effective recording is NotCommitted
Should not occur in normal operation (NotCommitted recordings are not in ERS, so the row never appears as UF, so the player can't Seal it via the UI). If it occurs through a load-time race state (journal finisher rolled MergeState back, slot.Sealed bit still on disk), reaper's NotCommitted-unconditional-no-reap rule (§6.5) prevents the RP from being deleted. **Defense-in-depth test.**

### 7.20 Pre-feature save load — Orbiting non-focus sibling from before upgrade
Legacy RP loads with `FocusSlotIndex == -1`. `noFocusSignal` short-circuit returns false for Orbiting/SubOrbital. The legacy Immutable Orbiting sibling stays Immutable; row does not appear in UF. **Forward-only migration for vessels.**

### 7.21 Pre-feature save load — Crashed sibling from before upgrade
Legacy RP loads with `FocusSlotIndex == -1`. Crashed branch returns true regardless of focus. Row appears in UF as it did in v0.9. **Regression guard.**

### 7.22 Pre-feature save load — stranded EVA kerbal from before upgrade
Legacy RP loads with `FocusSlotIndex == -1`. EVA branch returns BEFORE the noFocusSignal short-circuit. Stranded kerbal qualifies. Row appears in UF post-upgrade. **Intentional retroactive carve-out** — see §9.2 CHANGELOG split note.

### 7.23 New post-feature RP, no slot was focused at split time
RP captures with explicit `FocusSlotIndex = -1` (player was focused on an unrelated vessel outside the split). Same `noFocusSignal` behavior as legacy RPs: Orbiting/SubOrbital suppressed; Crashed and EVA-stranded qualify. v2 Park allows the player to manually add Orbiting siblings from these rare RPs while the backing RP still exists.

### 7.24 BG-only multi-controllable split with all controllable Orbiting siblings
RP captures with `FocusSlotIndex = -1` (no focus involved). All sibling slots stay Immutable post-commit. No UF rows. RP reaps. Same outcome as legacy save case.

### 7.25 In-place re-fly merge ending Orbiting (Site B-2 behavior)
Player drives a re-fly merge through `MergeDialog.TryCommitReFlySupersede` (in-place path). Site B-1 classifier returns Orbiting + non-focus → CP. Site B-2's end state depends on which option (§6.3) is picked:

- **(B2-A) force-Immutable**: slot closes; row drops post-merge; chain extension via in-place blocked. v0.9 behavior preserved.
- **(B2-B) fresh-provisional**: in-place merge spawns a separate provisional, supersede chain has distinct old/new endpoints, slot stays CP, row stays in UF for further re-fly.
- **(B2-C) auto-Seal**: same as B2-A end state, but reached via `slot.Sealed = true` rather than MergeState flip.

The §11.3 investigation picks one. The test plan in §13 starts with a (B2-A) baseline and extends if (B2-B) or (B2-C) is picked.

### 7.26 Hybrid star+linear supersede graph
Save loaded with legacy star portion `{probeOrig -> probeReFly1, probeOrig -> probeReFly2}` from a pre-§6.4 chain extension. Player invokes a new re-fly into the same slot. New invocation computes `priorTip = slot.EffectiveRecordingId(supersedes)` — the walker picks the oldest member of the star (probeReFly1) and walks linearly from there. New relation appended: `{probeReFly1 -> probeReFly3}`. Resulting graph is hybrid: star portion preserved, linear portion grows from probeReFly1. `TryResolveRewindPointForRecording(probeReFly3, ...)` returns the slot via the forward walk from slot.OriginChildRecordingId → probeReFly1 → probeReFly3. **Tolerated, no migration sweep needed.**

### 7.27 Crash-quit during a stable-leaf re-fly
Marker validates against on-disk session-provisional + RP. Session resumes. **No new state vs v0.9.** `MarkerValidator.Validate` extension weakly validates `SupersedeTargetId`; failure clears the field and AppendRelations falls back to `OriginChildRecordingId`.

### 7.28 Player reverts a stable-leaf re-fly mid-flight
`RevertInterceptor` 3-option dialog appears. Discard Re-fly preserves the parked row in UF (origin RP promoted, slot still CP). **Same as v0.9** (the in-place force in §6.3 Site B-2 doesn't affect Discard Re-fly because the Discard path doesn't reach the merge classifier).

### 7.29 Tree discard during chain-extension state
`TreeDiscardPurge.PurgeTree` removes every RP, supersede relation, and tombstone scoped to the discarded tree. The new `slot.Sealed` flags on the tree's RP slots are removed alongside the RPs. `marker.SupersedeTargetId` clears alongside the marker. **No new code paths needed in TreeDiscardPurge** — the discard already removes the parent objects.

### 7.30 Recording with no terminal state
`TerminalStateValue` is null (finalization didn't run cleanly). `TerminalOutcomeQualifies` returns false → not UF. Logged as `reason=noTerminal`.

### 7.31 Post-feature save loaded on pre-feature (v0.9.x) Parsek
Not supported. The new ConfigNode keys (`sealed`, `focusSlotIndex`, `supersedeTargetId`) would be silently dropped on next save. The player would lose the Sealed state and the FocusSlotIndex; UF predicate would degrade to the v0.9 Crashed-only behavior on the now-modified save. **No downgrade path provided.**

---

## 8. What Doesn't Change

- **v0.9 Crashed UF behavior.** Every recording that was UF under v0.9 is still UF under v1 (preserved by §7.21 + §7.9). Site A's broader predicate subsumes the v0.9 Crashed-only check.
- **Reaper's "every slot closed → reap" rule.** Same shape as v0.9; the close definition extends by one term (`slot.Sealed == true`) and tightens by one (NotCommitted unconditional no-reap).
- **Runtime ghost suppression.** `ComputeSessionSuppressedSubtree(marker)` returns the same closure as today (rooted at slot's immutable origin). Ghost playback engine, chain walker, ghost map presence, watch mode are unchanged.
- **`marker.OriginChildRecordingId` contract.** Still the slot's immutable origin. Existing consumers (`RevertInterceptor.FindSlotForMarker`, in-place continuation, ghost suppression) continue to read it unchanged.
- **CommitTombstones tombstone scope.** Still uses the closure from AppendRelations; the closure now correctly walks the prior-tip's descendants on chain extension (more accurate, not different in shape).
- **Recording sidecar format.** No changes to `.prec` / `_vessel.craft` / `_ghost.craft` / `.pcrf`.
- **Discard Re-fly + Retry from RP semantics.** Both run before the merge classifier; Site B-1 / Site B-2 changes don't affect them.
- **TreeDiscardPurge.** New fields are removed alongside their parent objects; no purge-side changes.
- **ERS / ELS routing rules.** No new raw-read sites; the new classifier reads through `EffectiveState.ComputeERS()` like every other consumer.
- **EVA kerbal-death tombstone scope.** v0.9 §6.13 narrow scope is unchanged. The EVA carve-out in this feature affects only the predicate (which rows show), not the tombstone-eligibility classifier.

---

## 9. Backward Compatibility

### 9.1 Pre-v1 saves on v1+ Parsek

- Load cleanly. The three new ConfigNode keys (`sealed`, `focusSlotIndex`, `supersedeTargetId`) are absent on disk. Default values match v0.9 behavior:
  - `slot.Sealed = false` → no slots are pre-Sealed; existing CP slots stay open.
  - `RewindPoint.FocusSlotIndex = -1` → the `noFocusSignal` short-circuit suppresses Orbiting/SubOrbital qualification on every legacy RP. **Forward-only migration for vessels.**
  - `marker.SupersedeTargetId = null` → AppendRelations coalesces with `?? OriginChildRecordingId` fallback. **No change in supersede behavior for legacy markers** (they continue to produce star-shaped graphs from the prior re-fly invocation).

- Stranded EVA kerbals from legacy saves DO retroactively appear in UF — the EVA branch returns BEFORE the noFocusSignal short-circuit. **Intentional retroactive carve-out.**

### 9.2 CHANGELOG

The migration + behavior-change story splits into three notes that must be presented separately to the player:

> **Vessels: forward-only.** Past missions where you deployed probes or stages and left them parked are not retroactively converted to Unfinished Flights. The feature only surfaces splits made after the upgrade. (Why: legacy data has no focus signal, and we'd rather under-include than flood your list with every routine upper stage from your career.)

> **Stranded EVA kerbals: retroactive.** EVA kerbals who got stranded on a body in past missions DO appear in Unfinished Flights after the upgrade. Click Fly to attempt a rescue. (Why: stranded kerbals are unambiguous — you almost certainly want them back — unlike orbital siblings where intent is unclear.)

> **In-place re-fly merges close the slot on stable terminals.** When you click Continue Flying through the in-place merge dialog (instead of letting Parsek spawn a fresh provisional), an Unfinished Flight that ends Orbiting / SubOrbital / EVA-stranded closes the slot via MergeState — the Fly button on that row disappears and the rewind point becomes eligible for cleanup. Re-flying that slot again requires a fresh split with its own RewindPoint. (Why: the in-place merge path uses one Recording for both the slot's effective state AND the supersede target, so leaving it CommittedProvisional would create a row that never goes away. The fresh-provisional merge path — the default in most UI flows — keeps the slot open for chain extension as today.) See §6.3 (B2-A) and §11.3 for context; the implementation PR may revise this behavior to (B2-B) or (B2-C) per the §11.3 investigation.

### 9.3 v1+ saves on pre-v1 (v0.9.x) Parsek

Not supported. Per §7.31.

### 9.4 Hybrid supersede graphs

Per §6.4 migration concern + §7.26: legacy star-shaped supersede portions in pre-§6.4 chains stay as-is. New post-feature chains extend linearly from whichever leaf the walker picks in the star portion. No migration sweep.

---

## 10. Risks

- **Disk usage growth.** Stable-leaf RPs persist until Sealed or all sibling slots close. Bounded by player diligence with the Seal button + by the FocusSlotIndex gate (which prevents routine focus-continuation upper stages from inflating the count). Mitigation: the Settings → Diagnostics RP disk-usage line includes live crashed-open, stable-open, and sealed-pending RP buckets next to the byte/file total.

- **Over-inclusion.** The simple terminal-state classifier surfaces some cases where the player meaningfully flew or didn't intend to leave a vessel and it ended Orbiting/SubOrbital (Mun-transfer-and-park, briefly-nudged-probe, deep-space probe carried by transfer stage and undocked there). The Seal button is the design's answer. **Do not re-introduce voluntary-action heuristics** — that path was explicitly rejected in R1-R3.

- **Predicate drift between Site A and Site B-1.** Both predicate-evaluation call sites must route through `UnfinishedFlightClassifier.Qualifies` with the same `(rec, slot, rp)` triple. A drift would mean rows that disappear immediately after a merge that should have kept them visible. Shared-classifier identity test in §13 guards against this. (Site B-2 is downstream of Site B-1's verdict and does not evaluate the predicate directly; the relevant Site B-2 risk is the in-place duplicate-row invariant below.)

- **Reversal of v0.9 §7.31 stance.** v0.9 said "stable-end splits explicitly not in scope." v1 says some non-focus stable-end splits ARE in scope (Orbiting/SubOrbital non-focus, EVA-stranded). Focus-continuation stable terminals continue to NOT get a row, preserving the "your mission's upper stage didn't suddenly become unfinished" intuition. The CHANGELOG must be precise about what changed and what didn't.

- **Breakup-survivor with stable-orbit terminal isn't auto-added to UF** (§7.11). The single most "obvious-feeling-bug" outcome of the focus-slot exclusion. Player remembers a structural failure, looks for it under UF, doesn't find it (because they survived to orbit). **Accepted v1 limitation.** Mitigations: player can crash the post-breakup vessel, or use v2 Park while the backing RP still exists. CHANGELOG must surface this with an FAQ-style entry; consider a forum/Discord post too.

- **Site B-2 in-place duplicate-row invariant.** The current v0.9 `MergeDialog.TryCommitReFlySupersede` override exists because the in-place path has no separate provisional — leaving the recording CP after merge would create a duplicate / un-reapable UF row. §6.3 enumerates three candidate handlings with a preference order: **(B2-B) fresh-provisional first** (cleanest gameplay, keeps the chain-extension promise on the in-place path), **(B2-A) force-Immutable as fallback** (preserves v0.9 behavior exactly when B2-B is out of v1 scope), avoid **(B2-C) auto-Seal** (muddies the player-explicit Seal semantic). §11.3 picks per the investigation. Until then, the design ships under the (B2-A) fallback assumption: the in-place re-fly's recording flips to `MergeState.Immutable` (closing the slot via MergeState — distinct from the player-explicit `ChildSlot.Sealed` action elsewhere in the design), chain extension via in-place is unavailable, and the player must use a fresh split RP to re-fly again. Documented in §9.2 CHANGELOG note 3 (which the implementation PR retracts if B2-B is picked instead).

- **Legacy hybrid supersede graphs.** §7.26 — tolerated by the design but the §11 hybrid test must run green for confidence. If the walker behavior on hybrids differs from expectations, a migration sweep may need to be added back.

- **EVA stranded edge cases.** What if KSP unloaded the kerbal mid-EVA and the terminal is unreliable? The `parsek-recording-finalization-design.md` work is the upstream contract. This feature inherits whatever finalization reliability that work delivers.

- **Optimizer chain length (RESOLVED).** Earlier drafts flagged eccentric-orbit BG-recorded vessels with periapsis-grazing atmosphere as a possible source of unbounded chains. Resolved by the `optimizer-meaningful-split-rule.md` investigation: `BackgroundOnRailsState` omits `currentTrackSection` / `trackSections` / `environmentHysteresis` entirely (`BackgroundRecorder.cs:157`), and `OnBackgroundPhysicsFrame` early-returns on `bgVessel.packed`. On-rails BG vessels can't generate optimizer-splittable Atmospheric↔ExoBallistic toggles. Guarded by `EccentricOrbitOptimizerInvariantTests`. No action needed for this feature.

---

## 11. Prerequisites and Future Work

### 11.1 Prerequisite v0.9 PR: invocation linearization

This feature requires the §6.4 marker-write change + closure-helper split to land first as a separate v0.9 PR. The prerequisite PR:

- Adds `marker.SupersedeTargetId` field with weak `MarkerValidator.Validate` extension.
- Modifies `RewindInvoker.AtomicMarkerWrite` to compute `priorTip` and stamp both marker fields + the provisional (guarded for in-place).
- Splits `EffectiveState.ComputeSessionSuppressedSubtree(marker)` into the public wrapper + new `ComputeSubtreeClosureInternal(marker, rootOverride)`.
- Modifies `SupersedeCommit.AppendRelations` to call the internal helper with `marker.SupersedeTargetId ?? marker.OriginChildRecordingId`.

The prerequisite PR is reviewable on its own. Its main test plan:

- Marker-write linearization: assert `AtomicMarkerWrite` stamps both marker fields correctly on both the in-place and fresh-provisional branches.
- Wrapper contract: assert `ComputeSessionSuppressedSubtree(null)` returns empty, `ComputeSessionSuppressedSubtree(marker-with-null-origin)` returns empty (cached-null fallback), and the return is a defensive copy (mutating the result doesn't affect the cache).
- Closure-equivalence: assert `ComputeSubtreeClosureInternal(marker, marker.OriginChildRecordingId)` returns identical results to today's `ComputeSessionSuppressedSubtree(marker)` for arbitrary marker/scenario combinations (refactor-equivalence regression guard).
- Linear chain extension: build a 3-link supersede chain via two re-flies; assert the second re-fly's relation is `{probeReFly1 -> probeReFly2}` (linear, not `{probeOrig -> probeReFly2}` star).

### 11.2 Build-phase open question: v0.9 §7.43 chain-extension test status

The v0.9 design doc claims `MergeCrashedReFlyCreatesCPSupersedeTest` is "Shipped (test)" for chain extension. R9-R15 of the research note's analysis implies that the second crashed re-fly's `EffectiveRecordingId` walk would silently resolve to the first re-fly under the star-graph code, which would silently break v0.9 chain extension.

The implementation PR's pre-work must run that test against current `main` and determine:

- (a) The test is green because of an unstated mechanism (relation insertion order, walker tie-break) that makes star-graph resolution work in practice. Document it.
- (b) The test is silently broken and nobody has noticed. File a `docs/dev/todo-and-known-bugs.md` entry; the prerequisite PR ships as the fix for that bug.

### 11.3 Build-phase open question: Site B-2 in-place handling

`MergeDialog.TryCommitReFlySupersede`'s in-place path uses the same `Recording` instance as both the slot's effective recording AND the supersede target — there is no separate provisional. The current v0.9 unconditional `provisional.MergeState = MergeState.Immutable` override after `FlipMergeStateAndClearTransient` exists because leaving the recording CP would create a duplicate / un-reapable UF row (the recording is its own effective; the slot stays CP; the predicate keeps qualifying it).

The implementation PR's pre-work must:

1. Read `MergeDialog.TryCommitReFlySupersede` and confirm the duplicate-row failure mode actually occurs without the override (write a regression test that builds the in-place case and asserts the row stays in UF post-merge under naive removal).
2. Pick one of the three handling options from §6.3:
   - **(B2-A) Force Immutable.** Preserve v0.9 behavior. Chain extension via in-place blocked. Simplest, lowest risk.
   - **(B2-B) Switch in-place to fresh-provisional.** Architectural change to `MergeDialog.TryCommitReFlySupersede`. Cleanest semantically; non-trivial code change. Pre-work: confirm fresh-provisional path is reachable from in-place context (vessel state, scenario state, etc.).
   - **(B2-C) Auto-Seal on in-place CP merge.** Set `slot.Sealed = true` whenever Site B-1 returned CP for an in-place merge. Hack-feeling; trivial code change. Pre-work: confirm auto-Seal logs make sense (`reaperImpact=willReap` etc.).
3. Document the choice + rationale in the prerequisite PR description and update §6.3 / §7.25 / §10 / §13.1 to remove the "deferred" framing.

**Preferred outcome: (B2-B)**. Cleanest gameplay model — the design's promise of "stable unfinished re-flights chain naturally" holds for both merge paths. A player who in-place-merges a stable-leaf re-fly twice doesn't hit arbitrary slot-closure friction. The investigation should bias toward shipping B2-B in v1 if the in-place path's re-architecture is feasible without a deep refactor.

**Fallback: (B2-A)** if the investigation reveals B2-B requires changes to `MergeDialog.TryCommitReFlySupersede` that are out of v1 scope. Preserves v0.9 behavior exactly; CHANGELOG note 3 (in §9.2) documents the chain-extension limitation. Acceptable v1 outcome; B2-B becomes a v2 follow-up.

**Avoid (B2-C)** unless both B2-B and B2-A are blocked. Auto-firing the Seal action from a merge code path muddies the per-row Seal semantic — Seal is meant as an explicit player intent ("I'm done with this slot"), and an auto-Seal makes the audit log ambiguous (was the seal a player action or a system action?). Players who use Seal manually expect it to mean "I explicitly closed this." Reserve B2-C only for the case where neither of the cleaner options is implementable.

### 11.4 v2 Park-from-not-UF affordance

Implemented as a follow-up. The Recordings table shows a `Park` button for stable-terminal leaves that the default predicate excludes (rover-drove-20m, breakup-survivor-orbiting, focus-continuation upper stage the player decided to come back to) when the row still resolves to a live Rewind Point slot. The action sets `slot.Parked` / `slot.ParkedRealTime`, leaves the recording and `MergeState` unchanged, makes the row qualify as an Unfinished Flight, and from there exposes the same `Fly` + `Seal` affordance. `RewindPointReaper` treats an unsealed parked `Immutable` slot as open while the classifier still qualifies it; Park does not resurrect already-reaped RP quicksaves. Under the B2-A in-place merge policy, confirming an in-place re-fly clears `slot.Parked` before forcing `Immutable`, so the in-place path still closes the slot.

### 11.5 v2 Auto-purge of long-lived sealed RPs

Out of scope for v1. The Settings → Diagnostics RP disk-usage line plus the §11.6 split buckets are the monitoring story. A later v2 could add a TTL-based reaper extension or a "Wipe All Sealed RPs" button, but this design does not invent destructive cleanup policy.

### 11.6 v2 Split disk-usage diagnostic

Implemented as a read-only follow-up. The Settings → Diagnostics line still shows total RP disk usage, and now also reports live RP buckets: crashed-open RPs, stable-open RPs, and sealed-pending RPs. Buckets are explanatory and may overlap when a single RP contains both sealed and still-open slots; this is monitoring only, not an auto-purge trigger.

---

## 12. Diagnostic Logging

Every predicate gate and state transition emits a log line. The tag catalog:

### 12.1 `[UnfinishedFlights]`

Predicate verdicts (Verbose; rate-limited per `(rec, reason)` pair):

```
IsUnfinishedFlight=false rec=<rid> reason=mergeState:<state>
IsUnfinishedFlight=false rec=<rid> reason=notControllable headIsDebris=true
IsUnfinishedFlight=false rec=<rid> reason=noParentBp
IsUnfinishedFlight=false rec=<rid> reason=noScenario
IsUnfinishedFlight=false rec=<rid> reason=slotSealed slot=<idx>
IsUnfinishedFlight=false rec=<rid> reason=downstreamBp
    chainTipChildBp=<bpId> matchedRpBp=<bpId>
IsUnfinishedFlight=false rec=<rid> reason=stableTerminalFocusSlot
    slot=<idx> focusSlot=<idx> terminal=<state>
IsUnfinishedFlight=false rec=<rid> reason=stableTerminal terminal=<state>
IsUnfinishedFlight=false rec=<rid> reason=noTerminal
IsUnfinishedFlight=false rec=<rid> reason=noFocusSignalOrbiting
    terminal=<state>
IsUnfinishedFlight=true  rec=<rid> reason=crashed terminal=Destroyed
IsUnfinishedFlight=true  rec=<rid> reason=stableLeafUnconcluded
    slot=<idx> terminal=<state>
IsUnfinishedFlight=true  rec=<rid> reason=strandedEva terminal=<state>
```

Site A promotion (Info, one-shot per promotion):

```
[UnfinishedFlights] Info: CommitTree promoted rec=<rid>
    slot=<slotIdx> rp=<rpId>
    reason=<crashed|stableLeafUnconcluded|strandedEva>
```

Seal handler (Info):

```
[UnfinishedFlights] Info: Seal cancelled rec=<rid>
[UnfinishedFlights] Info: Sealed slot=<slotIdx> rec=<rid>
    bp=<bpId> rp=<rpId> terminal=<state>
    reaperImpact=<willReap|stillBlocked>
[UnfinishedFlights] Error: Seal could not resolve slot for rec=<rid>
```

### 12.2 `[Supersede]`

Site B-1 flip:

```
[Supersede] Info: provisional=<rid> mergeState=<state> qualifies=<b>
    slot=<slotIdx> rp=<rpId> focusSlot=<rp.FocusSlotIndex>
[Supersede] Error: Site B-1 slot lookup failed for provisional=<rid>
    rpId=<marker.RewindPointId> originChildRec=<marker.OriginChildRecordingId>
    supersedeTargetId=<marker.SupersedeTargetId>
```

### 12.3 `[Rewind]`

Reaper:

```
[Rewind] Info: ReapOrphanedRPs reaped=<R> remaining=<rem>
    sealedSlotsContributing=<S>
```

### 12.4 `[ReFlySession]`

`MarkerValidator.Validate` extension:

```
[ReFlySession] Warn: Marker invalid field=SupersedeTargetId; clearing
```

(Existing `[ReFlySession]` lines unchanged.)

---

## 13. Test Plan

### 13.1 Unit tests

Round-trip:

- `ChildSlot.Sealed` + `SealedRealTime` round-trip through ConfigNode save/load; legacy `CHILD_SLOT` ConfigNodes without the keys load with `Sealed = false`. **Fails if:** key serialization changes silently break back-compat.
- `RewindPoint.FocusSlotIndex` round-trip; legacy `POINT` ConfigNodes load with `FocusSlotIndex = -1`. **Fails if:** the field migration silently changes legacy semantics.
- `ReFlySessionMarker.SupersedeTargetId` round-trip; legacy markers load with null. **Fails if:** the new validator chokes on legacy markers.

Predicate gates (`UnfinishedFlightClassifierTests`):

- Controllable-subject gate: `IsDebris == true` → false; `IsDebris == false` → proceed. **Fails if:** debris recordings start appearing as UF.
- Per-state TerminalOutcomeQualifies matrix: 8 `TerminalState` values × 2 `isFocus` permutations × 2 noFocusSignal permutations × 2 EVA-vs-vessel permutations = covers every branch of §6.1. **Fails if:** any branch's verdict regresses.
- Null-terminal: returns false. **Fails if:** unfinalized recordings get auto-promoted.
- EVA branch returns BEFORE noFocusSignal short-circuit: `EvaCrewName != null` + `FocusSlotIndex == -1` + Landed → true. **Fails if:** the retroactive EVA carve-out gets accidentally suppressed.
- Per-RP leaf gate: chainTip.ChildBranchPointId == null → proceed; chainTip.ChildBranchPointId == matchingRP.BranchPointId (breakup-survivor) → proceed; chainTip.ChildBranchPointId == otherBp.Id → rejected. **Fails if:** breakup-survivor regression returns or downstream-BP recordings start appearing as UF.
- Slot-closed gate: `slot.Sealed == false` → proceed; `slot.Sealed == true` → rejected. **Fails if:** Sealed slots persist as UF rows.

Site A / Site B-1 predicate-evaluation tests:

- Site A: stable-leaf non-focus controllable → CP; stable-leaf focus controllable → Immutable; stable-leaf debris → Immutable; crashed-leaf focus → CP (active-parent crash regression guard); crashed-leaf non-focus → CP. **Fails if:** the broader predicate misclassifies any case.
- Site B-1: re-fly ending Orbiting + non-focus → CP; re-fly ending Orbiting + focus → Immutable; re-fly ending Crashed → CP (regression); re-fly ending EVA-stranded Landed → CP. **Fails if:** chain extension on stable terminals breaks.
- Shared-classifier identity test: assert that Site A and Site B-1 paths resolve the same answer on the same `(Recording, ChildSlot, RewindPoint)` triple for every terminal-state value. **Fails if:** either predicate-call site starts using a different classifier or skipping a gate.

Site B-2 in-place handling test (consumes Site B-1's verdict, does not re-evaluate the predicate):

- Site B-2 (B2-A baseline): in-place re-fly ending Orbiting/SubOrbital/EVA-stranded → Immutable (preserves v0.9 behavior; row drops from UF post-merge; chain extension via in-place blocked). If §11.3 picks B2-B (fresh-provisional): assert CP and chain-extensible. If §11.3 picks B2-C (auto-Seal): assert `slot.Sealed == true` and MergeState == CP. **Fails if:** the chosen Site B-2 option regresses, OR the in-place path produces a duplicate / un-reapable UF row.

Seal handler:

- `slot.Sealed` flips + `SealedRealTime` stamps; `SupersedeStateVersion` bumps; `MergeState` UNCHANGED. **Fails if:** the design's slot-vs-MergeState decoupling regresses.
- `RewindPointReaper.IsReapEligible`: slots Immutable + Sealed → reap; any unsealed CP slot → no-reap; **any NotCommitted → no-reap regardless of slot.Sealed** (R16 P2.R regression guard — defends against load-time race states). **Fails if:** Sealed slots can reap RPs whose effective recording is mid-merge.
- Sealing one of N siblings with the others still CP → log emits `reaperImpact=stillBlocked`; sealing the last one → `reaperImpact=willReap`.

`RewindPointAuthor.Begin`:

- `FocusSlotIndex` set correctly when active vessel matches one of the post-split slots.
- `FocusSlotIndex == -1` when no slot matches (e.g., split where active vessel is pre-split parent that is NOT in the slot list).

### 13.2 Migration tests

- Legacy save with already-reaped split RPs has empty UF group for those splits (no retroactive surfacing). **Fails if:** the upgrade tries to resurrect deleted RPs.
- Legacy save with live RPs whose Landed siblings are Immutable: row count unchanged from v0.9. **Fails if:** Landed terminals start qualifying.
- Legacy save with live RPs whose Orbiting non-focus siblings are Immutable: `ApplyRewindProvisionalMergeStates` does NOT re-promote. **Fails if:** the noFocusSignal short-circuit doesn't fire on legacy RPs.
- Legacy save with live RPs whose stranded EVA Landed: row newly appears. Negative variant: same scenario with `EvaCrewName == null` → row does NOT surface. **Fails if:** the EVA carve-out doesn't fire OR vessel terminals get retroactively surfaced.
- Post-upgrade fresh deploy: spawn a multi-controllable split, leave probes orbiting, commit. Verify the new RP has `FocusSlotIndex >= 0`, the probes promote to CP, and the row appears in UF. **Fails if:** the motivating case stops working for new RPs.

### 13.3 Log assertion tests

- `[UnfinishedFlights] Verbose` reasons for each predicate gate appear with the right structured values. **Fails if:** the diagnostic catalog drifts from the predicate.
- `[UnfinishedFlights] Info: Sealed ... reaperImpact=...` log appears with the correct impact computation.
- `[Supersede] Error: Site B-1 slot lookup failed ...` log appears in the (intentionally-induced) test scenario where the §11.1 prerequisite is broken.

### 13.4 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`)

- 4-probe deploy from Mun mothership: fly mothership home, return to Recordings Manager, verify all 4 probes in UF AND mothership NOT in UF, Fly probe #2, land it, merge, verify slot closure + supersede chain extension. **Fails if:** the canonical case stops working end-to-end.
- Auto-parachute booster (S3, S19): verify NOT in UF and RP reaps cleanly when upper stage commits. **Critical regression guard: the largest-impact failure mode of an over-broad predicate.**
- Inverted scenario (S5): fly booster, leave upper stage Orbiting, verify upper stage IS in UF and RP stays alive.
- Stranded EVA (S6): kerbal stranded + lander Orbiting → both in UF; Fly kerbal, reboard, merge → kerbal slot closes; lander slot still in UF; Seal lander to clean up.
- Breakup-survivor (S7-S11): trigger a breakup, survive, land safely → NOT in UF and RP reaps. Trigger another, survive but Orbiting → NOT in UF (focus exclusion — documented limitation). Trigger a third, crash post-survival → IS in UF (Crashed regardless of focus).
- Cross-tree dock during stable-leaf re-fly (S12): probe re-fly docks with another tree's station, merge, verify slot closes Immutable, supersede stays inside probe's tree, station's tree unchanged.
- Re-fly chain extension on a stable terminal (S13): auto-included non-focus probe, re-fly to a different stable orbit, merge, verify slot stays CP and UF still shows the probe with the new flight as effective; re-fly again, land it, merge, verify slot now Immutable. Manual-Park variant: Park a default-excluded stable row, verify the parked flag keeps Landed/Orbiting outcomes in UF until Seal.
- Seal-and-no-unseal: Seal a row, verify there's no in-game un-seal path (Full-Revert is the only undo).

### 13.5 Hybrid graph regression test

Per §7.26: build a save with a legacy star portion `{probeOrig -> probeReFly1, probeOrig -> probeReFly2}` (representing pre-§6.4 chain extension where v0.9 wrote two relations sharing the same source). Player invokes a new post-feature re-fly into the same slot. The new invocation computes `priorTip = slot.EffectiveRecordingId(supersedes)`; the v0.9 walker scans from the beginning and stops at the first match, picking probeReFly1 (the older relation in insertion order). The new linearization writes `{probeReFly1 -> probeReFly3}` per the §6.4 marker-write recipe.

Resulting graph: star portion preserved (`{probeOrig -> probeReFly1, probeOrig -> probeReFly2}`) + linear extension from probeReFly1 (`{probeReFly1 -> probeReFly3}`). probeReFly2 is now an orphan branch — still in supersedes but unreachable from the dominant walk path.

Assert:
- `slot.EffectiveRecordingId(supersedes)` resolves to `probeReFly3` after the new relation is appended (walker: probeOrig → probeReFly1 [first match] → probeReFly3 [first match] → terminate).
- `TryResolveRewindPointForRecording(probeReFly3, ...)` returns the slot via the forward walk from `slot.OriginChildRecordingId → probeReFly1 → probeReFly3`.
- `TryResolveRewindPointForRecording(probeReFly2, ...)` ALSO returns the slot (probeReFly2 is in the slot's forward trail via `{probeOrig -> probeReFly2}`, even though the dominant walk picks the probeReFly1 branch).

**Fails if:** the helper can't handle hybrid topologies — in which case a migration sweep would need to be added to flatten star relations to linear before the prerequisite PR ships.

---

*End of design specification.*
