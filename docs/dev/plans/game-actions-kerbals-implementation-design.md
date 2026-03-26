# Kerbals Module — Implementation Design

*Implementation-ready specification for the kerbal reservation, replacement chain, and roster management system. Phased by game mode complexity: Sandbox → Science → Career.*

**Design doc reference:** `docs/parsek-game-actions-system-design.md` section 9
**Spike findings:** `docs/dev/plans/game-actions-spike-findings.md` (Spike C)
**Existing code:** `Source/Parsek/CrewReservationManager.cs` (497 lines, will be significantly expanded)

---

## 1. Current State Analysis

### 1.1 What Exists

`CrewReservationManager.cs` implements a **flat name→replacement mapping**:
- `crewReplacements` dict: reserved kerbal name → replacement kerbal name
- `ReserveCrewIn()`: iterates snapshot PART nodes, hires replacement with same trait via `KerbalRoster.GetNewKerbal()`
- `SwapReservedCrewInFlight()`: swaps reserved crew out of active vessel
- `UnreserveCrewInSnapshot()`: sets crew back to Available at spawn time
- Persisted as `CREW_REPLACEMENTS` ConfigNode with ENTRY sub-nodes

### 1.2 What's Missing (Gaps from Investigation)

| Gap | Description |
|-----|-------------|
| **No per-crew end state** | `TerminalState` is vessel-level only. No tracking of individual crew fate (RECOVERED/DEAD/MIA/STRANDED). |
| **No replacement chains** | Current system is flat (one replacement per reserved kerbal). No chain depth, no slot ownership. |
| **No retired pool** | Used stand-ins are left in the roster with no special status. |
| **No UT-based reservation** | Current reservation is binary (reserved or not). No UT=0→endUT temporal model. |
| **No dismissal protection** | No Harmony patch prevents dismissing Parsek-managed kerbals. |
| **No recalculation** | Reservation is computed once at commit time, not recomputed from scratch on rewind. |
| **STRANDED not detectable** | Vessel landed on remote body is indistinguishable from successful landing. |
| **Crew death during recording not captured** | No per-crew death events on Recording. `SegmentEventType.CrewLost` defined but never emitted. |

### 1.3 Integration Points (from Investigation)

**Commit flow** (5 paths, all follow the same crew pattern):
1. `RecordingStore.CommitPending()` — moves recording to committed list
2. `CrewReservationManager.ReserveSnapshotCrew()` — iterates ALL committed recordings, reserves crew
3. `CrewReservationManager.SwapReservedCrewInFlight()` — swaps crew on pad vessel

**Rewind flow** (2 paths):
- **Rewind (go-back):** in-memory `crewReplacements` preserved, `ReserveSnapshotCrew()` re-reserves
- **Revert (quickload):** `LoadCrewReplacements()` from launch quicksave (potentially stale), `ReserveSnapshotCrew()` re-reserves

**Spawn flow:**
1. `RemoveDeadCrewFromSnapshot()` — strips Dead/Missing crew
2. `EnsureCrewExistInRoster()` — creates missing kerbals
3. `RemoveSpecificCrewFromSnapshot()` — strips EVA'd crew
4. `UnreserveCrewInSnapshot()` — sets crew back to Available
5. `RemoveDuplicateCrewFromSnapshot()` — prevents duplicates

**Crew data on Recording:**
- `VesselSnapshot` ConfigNode: crew names in PART→crew values
- `EvaCrewName`: EVA kerbal name (EVA child recordings only)
- `ExtractCrewFromSnapshot()`: canonical crew name extraction

---

## 2. Phased Implementation Plan

### Phase A: Sandbox Mode (This Phase)

**Scope:** Reservation + stand-ins + chains + retired pool + dismissal protection. No funds, no XP, no hiring costs. Kerbals are free and abundant. The goal is to prevent the duplicate kerbal paradox.

### Phase B: Science Mode (Future)

**Additional scope:** Same as sandbox but kerbal death has roster impact (shrinks by one, player creates replacement for free). No funds.

### Phase C: Career Mode (Future)

**Additional scope:** Hiring costs (funds spending), XP banking via flight logs, full replacement chain economics.

---

## 3. Data Model (Phase A)

### 3.1 Per-Crew End State on Recording

**New field on `Recording`:**

```csharp
/// <summary>
/// Per-crew end states for this recording. Derived from vessel TerminalState
/// and crew roster status at recording end. Keys are kerbal names.
/// Not serialized — recomputed at commit time from TerminalState + roster.
/// </summary>
public Dictionary<string, KerbalEndState> CrewEndStates;
```

**New enum:**

```csharp
// File: Source/Parsek/KerbalEndState.cs
public enum KerbalEndState
{
    Recovered  = 0,  // Vessel recovered, crew returned to KSC
    Landed     = 1,  // Vessel landed/splashed (not recovered yet — in progress or stranded)
    Orbiting   = 2,  // Vessel in orbit (not recovered yet)
    Dead       = 3,  // Crew killed during recording
    MIA        = 4,  // Crew missing (vessel removed by KSP)
}
```

**Mapping from vessel TerminalState:**

```csharp
internal static KerbalEndState InferCrewEndState(
    TerminalState? vesselState, ProtoCrewMember.RosterStatus rosterStatus)
{
    // Roster status takes priority — KSP sets this regardless of vessel state
    if (rosterStatus == ProtoCrewMember.RosterStatus.Dead)
        return KerbalEndState.Dead;
    if (rosterStatus == ProtoCrewMember.RosterStatus.Missing)
        return KerbalEndState.MIA;

    switch (vesselState)
    {
        case TerminalState.Recovered:
            return KerbalEndState.Recovered;
        case TerminalState.Destroyed:
            return KerbalEndState.Dead;
        case TerminalState.Landed:
        case TerminalState.Splashed:
            return KerbalEndState.Landed;
        case TerminalState.Orbiting:
        case TerminalState.SubOrbital:
            return KerbalEndState.Orbiting;
        case TerminalState.Docked:
        case TerminalState.Boarded:
            // Crew merged into the survivor vessel via dock or board.
            // They are alive and on the surviving vessel — treat as Recovered
            // (they'll be handled by the survivor's recording).
            return KerbalEndState.Recovered;
        default:
            return KerbalEndState.Orbiting; // conservative default for null
    }
}
```

**Design note on Docked/Boarded:** When a vessel docks or a kerbal boards, the recording's TerminalState is set to `Docked`/`Boarded`. The crew merged into the survivor vessel and are alive. We treat them as `Recovered` for reservation purposes — they will appear in the survivor recording's crew.

**Design note:** STRANDED is not in this enum. In Phase A, we don't distinguish "landed successfully" from "stranded." Both are `Landed`. The design doc's `STRANDED` is a future refinement requiring player input or heuristics (no fuel + remote body). For Phase A, `Landed` on a non-Kerbin body means the crew is unavailable until a rescue recording closes the reservation — functionally identical to STRANDED.

### 3.2 Reservation Model

**New class:** `KerbalReservation` (derived, not stored)

```csharp
/// <summary>
/// Per-kerbal reservation derived from committed recordings.
/// Recomputed from scratch on every recalculation.
/// </summary>
internal class KerbalReservation
{
    public string KerbalName;
    public double ReservedUntilUT;  // double.PositiveInfinity for permanent
    public bool IsPermanent;        // Dead or MIA — never freed
    public string SlotOwner;        // who this kerbal is a stand-in for (null if original crew)
    public List<string> ChainBelow; // stand-ins generated to fill this kerbal's slot
}
```

**Reservation rules (from design doc 9.2):**
- Reserved from UT=0 to `max(endUT)` across all recordings using this kerbal
- `Recovered` → temporary, `reservedUntilUT = recoveryUT`
- `Landed`/`Orbiting` → open-ended temporary, `reservedUntilUT = double.PositiveInfinity` (until rescue)
- `Dead`/`MIA` → permanent, `reservedUntilUT = double.PositiveInfinity`, `IsPermanent = true`

### 3.3 Replacement Chain Model

```csharp
/// <summary>
/// Per-slot replacement chain. The slot owner is the original kerbal.
/// Stand-ins fill the slot when the owner is reserved.
/// </summary>
internal class KerbalSlot
{
    public string OwnerName;        // original kerbal (e.g., "Jeb")
    public string OwnerTrait;       // "Pilot" / "Engineer" / "Scientist"
    public bool OwnerPermanentlyGone; // Dead or MIA — slot has no returnable owner
    public List<ChainEntry> Chain;  // ordered by generation (oldest first)
}

internal struct ChainEntry
{
    public string StandInName;    // persisted — same name across recalculations
    public bool UsedInRecording;  // derived — scan committed recordings for this name
    public bool IsReserved;       // derived — check if any recording has this kerbal
}
```

**Chain rules:**
- Active occupant = deepest non-reserved entry, or the owner if the owner is free
- When owner reclaims: all deeper stand-ins displaced (deleted if unused, retired if used)
- If a stand-in dies within a chain (owner is temporarily reserved): chain continues, new stand-in generated

### 3.4 Module State

```csharp
/// <summary>
/// The kerbals module. Computes reservation and chain state from committed recordings.
/// Pure computation — no KSP mutation. KSP state patching is separate.
/// </summary>
internal static class KerbalsModule
{
    // Derived state (recomputed on every recalculation)
    private static Dictionary<string, KerbalReservation> reservations;
    private static Dictionary<string, KerbalSlot> slots;
    private static HashSet<string> retiredKerbals;
    private static List<StandInAction> pendingStandIns; // stand-ins to create

    /// <summary>
    /// Recalculate all kerbal state from committed recordings.
    /// Called on: commit, rewind, revert, KSP load.
    /// Uses RecordingStore.CommittedRecordings directly (includes tree leaves).
    /// Tree leaves are already in CommittedRecordings after CommitTree().
    /// </summary>
    internal static void Recalculate()
    {
        var recordings = RecordingStore.CommittedRecordings;
        // Skip loop recordings and fully-disabled chains
        // (same filtering as existing ReserveSnapshotCrew)
        ...
    }

    /// <summary>
    /// Apply derived state to KSP roster. Creates stand-ins, removes unused
    /// stand-ins, sets roster statuses. Populates crewReplacements dict
    /// for SwapReservedCrewInFlight compatibility.
    ///
    /// Wraps mutations in SuppressCrewEvents = true.
    /// </summary>
    internal static void ApplyToRoster(KerbalRoster roster)
    {
        GameStateRecorder.SuppressCrewEvents = true;
        try
        {
            // 1. For each slot needing a stand-in:
            //    - If stand-in name already in roster: keep (deterministic)
            //    - If not: create via roster.GetNewKerbal(), set trait
            //    - Store in KERBAL_SLOTS for name persistence

            // 2. For each unused stand-in displaced by owner reclaim:
            //    - Remove from roster via roster.Remove()

            // 3. For each reserved kerbal:
            //    - Set rosterStatus = Assigned
            //    - Populate crewReplacements[reserved] = activeOccupant
            //      (bridge to SwapReservedCrewInFlight)

            // 4. For each retired kerbal:
            //    - Set rosterStatus = Assigned (unassignable)

            // 5. Invalidate ResourceBudget (existing pattern)
        }
        finally
        {
            GameStateRecorder.SuppressCrewEvents = false;
        }
    }

    /// <summary>
    /// Check if a kerbal is available for a new recording.
    /// Called at commit time to validate crew assignment.
    /// </summary>
    internal static bool IsKerbalAvailable(string kerbalName)
    { ... }

    /// <summary>
    /// Check if a kerbal is managed by Parsek (reserved, stand-in, or retired).
    /// Used by dismissal protection Harmony patch.
    /// </summary>
    internal static bool IsManaged(string kerbalName)
    { ... }

    /// <summary>
    /// Get the active occupant for a slot at the current timeline state.
    /// </summary>
    internal static string GetActiveOccupant(string slotOwnerName)
    { ... }
}
```

---

## 4. Recalculation Algorithm (Phase A)

The core algorithm. Pure computation, no KSP mutation.

```
Recalculate():
  1. Clear derived state (reservations, retired set).
     DO NOT clear slots — stand-in names persist across recalculations.

  2. Collect all crew assignments:
     For each committed recording in RecordingStore.CommittedRecordings:
       Skip if rec.LoopPlayback (loop recordings don't reserve crew)
       Skip if RecordingStore.IsChainFullyDisabled(rec.ChainId)
       crew = ExtractCrewFromSnapshot(rec.VesselSnapshot)
       endStates = rec.CrewEndStates (or infer from TerminalState if not populated)
       For each kerbalName in crew:
         endState = endStates[kerbalName] ?? KerbalEndState.Orbiting
         endUT:
           Recovered → rec.EndUT (temporary)
           Landed/Orbiting → double.PositiveInfinity (open-ended temporary)
           Dead/MIA → double.PositiveInfinity + IsPermanent=true (permanent)
         Add/merge reservation: take max endUT across recordings, permanent wins

  3. Build replacement chains (uses persisted slots for name stability):
     For each reservation where !IsPermanent:
       Find existing KerbalSlot (from persisted KERBAL_SLOTS) or create new
       Determine if owner is currently reserved (exists in reservations dict)
       If reserved and chain has no stand-in, OR last stand-in is also reserved:
         If existing slot already has a stand-in name at this depth: reuse it
         Else: generate new name via KerbalRoster.GetNewKerbal() — save name to slot
       Mark chain entries: UsedInRecording = (name appears in any committed snapshot)
       Mark chain entries: IsReserved = (name in reservations dict)

  4. Identify retired kerbals:
     For each slot, for each chain entry:
       If entry.UsedInRecording AND displaced (owner or predecessor is free):
         Add to retiredKerbals set

  5. Determine active occupants:
     For each slot:
       Walk chain from deepest to shallowest
       First non-reserved entry = active occupant
       If all reserved, the deepest pending stand-in = active occupant
```

**Stand-in naming determinism:** Stand-in names are generated once (via `KerbalRoster.GetNewKerbal()`) and persisted in KERBAL_SLOTS. On subsequent recalculations, the same name is reused from the persisted slot. This prevents roster orphans. Only when a new chain depth is needed (deeper than any existing entry) is a new name generated.

**Key invariant:** After recalculation, every slot has exactly one active occupant who is Available in the roster. The total available roster size is constant (original roster size minus permanently lost kerbals).

---

## 5. Integration with Existing Code

### 5.1 At Commit Time

**Where:** Every call site that invokes `RecordingStore.CommitPending()` or `CommitPendingTree()`. There are **14+ call sites** across 4 files. Rather than patching each one, the recalculation is centralized.

**Strategy:** Hook into `RecordingStore.CommitPending()`/`CommitPendingTree()` completion, or add a single `RecalculateAndApplyCrew()` method called from a centralized post-commit point.

**Current call sites for `ReserveSnapshotCrew()` (all need replacement):**
- `MergeDialog.cs`: lines 126, 188, 396, 810
- `ParsekFlight.cs`: lines 3602, 4694 (tree leaf reservation)
- `ParsekScenario.cs`: lines 507, 534, 686

**Current call sites for `CommitPending`/`CommitPendingTree` that do NOT call `ReserveSnapshotCrew` afterward (need addition):**
- `ParsekFlight.cs`: lines 1190, 1931, 1945, 4629, 7756
- `ParsekScenario.cs`: safety-net commits at lines 37, 44, 489, 495, 593, 606, 813
- `ChainSegmentManager.cs`: line 414

**Recommended approach:** Create `KerbalsModule.RecalculateAndApply()` that calls both `Recalculate()` and `ApplyToRoster()`:

```csharp
internal static void RecalculateAndApply()
{
    var roster = HighLogic.CurrentGame?.CrewRoster;
    if (roster == null) return;

    Recalculate();
    ApplyToRoster(roster);
}
```

Then grep for all `ReserveSnapshotCrew()` calls and replace with `KerbalsModule.RecalculateAndApply()`. Also add calls after commit paths that currently lack crew handling.

**Bridge to `SwapReservedCrewInFlight`:** `ApplyToRoster()` populates `CrewReservationManager.crewReplacements` dict as step 3 (mapping reserved kerbal → active occupant). This keeps `SwapReservedCrewInFlight()` working unchanged — it reads from `crewReplacements` to swap crew on the active vessel.

### 5.2 At Rewind/Revert Time

**Where:** `ParsekScenario.HandleRewindOnLoad()` (line 686) and `ParsekScenario.OnLoad()` (line 507).
**What:** Full recalculation from committed recordings.

```csharp
// Replace: CrewReservationManager.ReserveSnapshotCrew();
// With:
KerbalsModule.RecalculateAndApply();
```

### 5.3 At Spawn Time

**Where:** `VesselSpawner.RespawnVessel()` (line 41).
**No change needed** — the existing 5-step crew pipeline handles spawn correctly. `UnreserveCrewInSnapshot` sets crew back to Available so KSP can assign them to the spawned vessel.

### 5.4 Dismissal Protection

**New file:** `Source/Parsek/Patches/KerbalDismissalPatch.cs`

```csharp
[HarmonyPatch(typeof(KerbalRoster), nameof(KerbalRoster.Remove))]
internal static class KerbalDismissalPatch
{
    static bool Prefix(ProtoCrewMember %.crew)
    {
        // Allow Parsek's own cleanup calls through
        if (GameStateRecorder.SuppressCrewEvents) return true;
        if (GameStateRecorder.IsReplayingActions) return true;

        if (KerbalsModule.IsManaged(%.crew.name))
        {
            ParsekLog.Info("KerbalDismissal",
                $"Blocked dismissal of '{%.crew.name}' — managed by Parsek");
            // TODO: show dialog explaining why
            return false;
        }
        return true;
    }
}
```

---

## 6. Serialization

### 6.1 What Gets Persisted

Reservations and chains are **derived** — recomputed on every load. Only the inputs are persisted:

- `Recording.CrewEndStates` — persisted per-recording in the sidecar metadata
- `crewReplacements` dict — persisted in CREW_REPLACEMENTS ConfigNode (existing)
- Stand-in names — persisted implicitly (they exist in KSP's roster)

### 6.2 CrewEndStates Serialization

On the RECORDING ConfigNode in the .sfs metadata:

```
RECORDING
{
    ...existing fields...
    CREW_END_STATES
    {
        crew = Jebediah Kerman
        state = 0
        crew = Bill Kerman
        state = 0
    }
}
```

Alternating `crew`/`state` values (same pattern as existing paired ConfigNode values). State is the int value of `KerbalEndState`.

### 6.3 Stand-In Tracking

Stand-in kerbals need to be identifiable as Parsek-managed. Options:

**Format: `KERBAL_SLOTS` ConfigNode** (directly represents the chain model):

```
KERBAL_SLOTS
{
    SLOT
    {
        owner = Jebediah Kerman
        trait = Pilot
        CHAIN_ENTRY { name = Hanley Kerman }
        CHAIN_ENTRY { name = Kirrim Kerman }
    }
}
```

Note: `used` is not stored — it's derived by scanning committed recording snapshots for the stand-in name. This avoids stale data on rewind. The slot only persists **names and trait** for deterministic re-creation.

### 6.4 Backward Compatibility

Existing saves have `CREW_REPLACEMENTS` with flat `ENTRY { original; replacement }` nodes. Migration:

1. On load: if `KERBAL_SLOTS` node exists, load it. Done.
2. If no `KERBAL_SLOTS` but `CREW_REPLACEMENTS` exists: migrate.
   - For each ENTRY: create a SLOT with `owner = original`, `trait = (read from roster)`, one CHAIN_ENTRY with `name = replacement`.
3. On next save: write `KERBAL_SLOTS`. Keep writing `CREW_REPLACEMENTS` as well for one version (backward compat window).
4. After one version: stop writing `CREW_REPLACEMENTS`.

This ensures saves can be opened by older Parsek versions during the transition.

---

## 7. Diagnostic Logging

### State Transitions
- `[Parsek][INFO][Kerbals] Reservation: '{name}' reserved UT=0→{endUT:F1} ({endState}), recording '{recId}'`
- `[Parsek][INFO][Kerbals] Reservation extended: '{name}' endUT {oldUT:F1}→{newUT:F1} (new recording '{recId}')`
- `[Parsek][INFO][Kerbals] Permanent reservation: '{name}' ({endState}) — slot exits chain system`

### Chain Operations
- `[Parsek][INFO][Kerbals] Stand-in generated: '{standInName}' ({trait}) replaces reserved '{ownerName}'`
- `[Parsek][INFO][Kerbals] Chain depth {depth} for slot '{ownerName}': [{names joined by →}]`
- `[Parsek][INFO][Kerbals] Reclaim: '{ownerName}' free at UT={ut:F1}, displacing {count} stand-in(s)`
- `[Parsek][INFO][Kerbals] Stand-in '{name}' displaced: {deleted|retired} (usedInRecording={used})`

### Decisions
- `[Parsek][INFO][Kerbals] Commit check: '{name}' is {available|reserved} — commit {allowed|blocked}`
- `[Parsek][INFO][Kerbals] Dismissal blocked: '{name}' is managed by Parsek ({reason})`

### Recalculation
- `[Parsek][INFO][Kerbals] Recalculation: {recCount} recordings, {crewCount} crew, {reservationCount} reservations, {chainCount} chains, {retiredCount} retired`

---

## 8. Edge Cases

### E1: Multi-crew vessel, one crew dies (v1: vessel-level inference)
**Scenario:** 3-kerbal vessel, one kerbal's part destroyed mid-flight, vessel survives.
**Phase A behavior:** All crew get the vessel's TerminalState. The dead kerbal's roster status is `Dead` (set by KSP), so `InferCrewEndState` returns `Dead` for them, `Recovered`/`Landed` for others. Correct.

### E2: EVA kerbal on separate recording
**Scenario:** Jeb goes EVA, creating a child recording. Parent vessel has Bill+Val.
**Phase A behavior:** EVA recording has `EvaCrewName = "Jeb"`. Parent recording's snapshot still has Jeb in crew. At commit, `RemoveSpecificCrewFromSnapshot` removes Jeb from parent spawn snapshot. Both Jeb and the parent crew are reserved independently.

### E3: Same kerbal in multiple committed recordings (paradox prevention)
**Scenario:** Jeb in recording A (committed). Player rewinds. Jeb in recording B (attempt to commit).
**Phase A behavior:** `IsKerbalAvailable("Jeb")` returns false. Commit blocked. Player must use a different kerbal.

### E4: Deep replacement chain (depth 3+)
**Scenario:** Jeb reserved → Hanley generated → Hanley reserved → Kirrim generated → Kirrim reserved → Dunford generated.
**Phase A behavior:** Chain depth 3. Active occupant is Dunford. When Kirrim frees, Dunford displaced. When Hanley frees, Kirrim displaced. When Jeb frees, Hanley displaced.

### E5: Stand-in dies within temporary chain
**Scenario:** Jeb reserved (recovered at UT=2000). Hanley (stand-in) used in recording, dies.
**Phase A behavior:** Hanley is permanently reserved (Dead). Chain continues — Kirrim generated. When Jeb returns (UT=2000), Hanley retired (Dead+used), Kirrim deleted (unused) or retired (used).

### E6: Rewind recomputes all chains from scratch
**Scenario:** Deep chain state at UT=3100. Player rewinds to UT=500.
**Phase A behavior:** Full recalculation. Walk all committed recordings, rebuild reservations and chains. Stand-ins that were retired may become reserved again. Active occupants may change.

### E7: Slot owner permanently lost — no auto-replacement
**Scenario:** Jeb dies in recording (permanent reservation).
**Phase A behavior (sandbox):** Roster shrinks by one. Player can create a new kerbal at the Astronaut Complex for free (sandbox). The new kerbal is independent — not in Jeb's chain.

### E8: Recording with probe core (no crew)
**Scenario:** Unmanned probe recording committed.
**Phase A behavior:** `ExtractCrewFromSnapshot` returns empty list. No reservations. No chains.

### E9: Multiple recordings with overlapping crew, committed in non-chronological order
**Scenario:** Recording A (Jeb, UT=1000-2000, recovered) committed. Recording B (Jeb+Bill, UT=500-1500, recovered) attempted.
**Phase A behavior:** Jeb already reserved. Commit of B blocked. Bill is available (not in any recording). Player must fly B without Jeb.

### E10: Revert with stale crewReplacements from launch quicksave
**Scenario:** Player commits recording after launch, adding new replacements. Then reverts. Launch quicksave has old crewReplacements.
**Phase A behavior:** `KerbalsModule.Recalculate()` runs after load, recomputing the correct state regardless of what the quicksave had. This is the key advantage of derived-state recalculation over stored-state.

### E11: Kerbal exists in multiple tree leaf recordings
**Scenario:** Tree recording, Jeb starts on composite vessel. After undock, Jeb is on leaf A. Leaf B has no crew overlap.
**Phase A behavior:** Jeb reserved once (from leaf A). No duplication. Tree leaf recordings have separate snapshots.

### E12: Rapid commits (3 recordings committed quickly)
**Scenario:** Player commits A (Jeb), then B (stand-in Hanley), then C (stand-in Kirrim).
**Phase A behavior:** Each commit triggers full recalculation. After A: Jeb reserved, Hanley generated. After B: Hanley reserved, Kirrim generated. After C: Kirrim reserved, Dunford generated. Chain depth 3.

### E13: Astronaut Complex cap exceeded by stand-ins
**Scenario:** 5-kerbal cap. 4 originals + 3 stand-ins = 7 total in roster.
**Phase A behavior:** Cap only enforced at hiring UI. `GetNewKerbal()` always succeeds programmatically. Already confirmed by 3 existing callsites (Spike C finding).

### E14: Dismissal of non-Parsek kerbal should still work
**Scenario:** Player hires a kerbal manually and wants to dismiss them.
**Phase A behavior:** `KerbalsModule.IsManaged()` returns false for manually-hired unrecorded kerbals. Dismissal proceeds normally.

### E15: Loop recordings excluded from reservation
**Scenario:** A recording has `LoopPlayback = true`.
**Phase A behavior:** `Recalculate()` skips it (same as existing `ReserveSnapshotCrew` behavior). Loop recordings are visual-only replays, not committed timeline entries.

### E16: Disabled chain recordings excluded
**Scenario:** A chain is fully disabled by the player.
**Phase A behavior:** `Recalculate()` skips all recordings in the chain (same as existing `IsChainFullyDisabled` check). Disabled recordings don't reserve crew.

### E17: KSP respawns MIA kerbal while Parsek has permanent reservation
**Scenario:** Jeb is MIA (permanent reservation). KSP respawns Jeb after configurable delay.
**Phase A behavior:** Parsek's next `RecalculateAndApply()` will find Jeb in the roster with status Available (KSP respawned). But reservations dict still has Jeb as permanently reserved. `ApplyToRoster()` sets Jeb back to Assigned. KSP's respawn is effectively overridden. This is the conservative-by-design approach — MIA is permanent in Parsek until a future phase adds respawn support. (Design doc D7.)

### E18: Docked vessel recording — crew end state
**Scenario:** Vessel A docks into Vessel B. Recording A has TerminalState.Docked.
**Phase A behavior:** `InferCrewEndState` returns `Recovered` for crew on vessel A. They merged into B and are alive. Vessel B's recording will track them going forward.

### E19: EVA child recording crew overlaps with parent snapshot
**Scenario:** Jeb EVAs from a vessel. The parent snapshot still lists Jeb as crew.
**Phase A behavior:** `ExtractCrewFromSnapshot` on the parent returns Jeb. But `RemoveSpecificCrewFromSnapshot` removes Jeb from the parent spawn snapshot at spawn time. The reservation system sees Jeb in the EVA recording only (parent's Jeb is excluded via `BuildExcludeCrewSet`). No double-reservation.

---

## 9. Test Plan

### 9.1 Unit Tests (Pure Static Methods)

**`KerbalEndStateTests.cs`:**
1. `InferCrewEndState_Recovered_ReturnsRecovered` — fails if recovered crew treated as stranded (wrong reservation duration)
2. `InferCrewEndState_Destroyed_ReturnsDead` — fails if destroyed crew treated as alive (duplicate kerbal possible)
3. `InferCrewEndState_DeadRosterStatus_ReturnsDead` — fails if roster status not checked first (Dead crew on Landed vessel misclassified)
4. `InferCrewEndState_MissingRosterStatus_ReturnsMIA` — fails if Missing crew treated as available
5. `InferCrewEndState_Landed_ReturnsLanded` — fails if landed crew treated as recovered (premature slot reclaim)
6. `InferCrewEndState_NullTerminalState_ReturnsOrbiting` — fails if null causes exception instead of conservative default
6b. `InferCrewEndState_Docked_ReturnsRecovered` — fails if docked crew treated as orbiting (wrong reservation)
6c. `InferCrewEndState_Boarded_ReturnsRecovered` — fails if boarded crew treated as orbiting

**`KerbalReservationTests.cs`:**
7. `Recalculate_SingleRecording_ReservesAllCrew` — fails if crew extraction misses multi-crew vessel → duplicate kerbal paradox
8. `Recalculate_RecoveredCrew_TemporaryReservation` — fails if recovered uses PositiveInfinity → crew locked forever
9. `Recalculate_DeadCrew_PermanentReservation` — fails if dead crew not marked permanent → stand-in generated for dead crew
10. `Recalculate_MultipleRecordings_MaxEndUT` — fails if endUT not merged → crew freed too early between recordings
11. `Recalculate_NoCrewRecording_NoReservations` — fails if probe snapshot triggers reservation
12. `IsKerbalAvailable_Reserved_ReturnsFalse` — fails if available check doesn't consult reservations → duplicate kerbal
13. `IsKerbalAvailable_NotReserved_ReturnsTrue` — fails if unrecorded kerbals blocked → can't fly anyone
14. `IsKerbalAvailable_PermanentlyGone_ReturnsFalse` — fails if dead kerbal shown as available
14b. `Recalculate_SkipsLoopRecordings` — fails if loop recordings reserve crew → visual replays block crew
14c. `Recalculate_SkipsDisabledChains` — fails if disabled recordings reserve crew

**`KerbalChainTests.cs`:**
15. `Recalculate_ReservedCrew_GeneratesStandIn` — Jeb reserved → stand-in generated with same trait
16. `Recalculate_StandInReserved_GeneratesNextStandIn` — chain depth 2
17. `Recalculate_ChainDepth3_ThreeStandIns` — chain depth 3 (design doc 9.6 scenario)
18. `GetActiveOccupant_OwnerFree_ReturnsOwner` — Jeb not reserved → Jeb is active
19. `GetActiveOccupant_OwnerReserved_ReturnsStandIn` — Jeb reserved → stand-in is active
20. `GetActiveOccupant_AllReserved_ReturnsDeepest` — chain fully reserved → deepest stand-in is active
21. `Recalculate_OwnerReclaims_DisplacesStandIns` — owner free → unused stand-ins marked for deletion
22. `Recalculate_UsedStandInDisplaced_MarkedRetired` — used stand-in displaced → in retiredKerbals set
23. `Recalculate_UnusedStandInDisplaced_MarkedForDeletion` — unused → not in retired set
24. `Recalculate_StandInDiesInChain_ChainContinues` — stand-in Dead + owner temporary → new stand-in generated
25. `Recalculate_OwnerDead_NoAutoReplacement` — owner Dead → no stand-in, slot exits chain system

**`KerbalDismissalTests.cs`:**
26. `IsManaged_ReservedKerbal_ReturnsTrue`
27. `IsManaged_ActiveStandIn_ReturnsTrue`
28. `IsManaged_RetiredKerbal_ReturnsTrue`
29. `IsManaged_UnmanagedKerbal_ReturnsFalse`

### 9.2 Integration Tests

**`KerbalRecalculationIntegrationTests.cs`:**
30. `FullScenario_SimpleReservationAndReturn` — design doc 9.11 scenario 1
31. `FullScenario_DeepChainAllUsed` — design doc 9.11 scenario 2
32. `FullScenario_LandedOnRemoteBody_OpenEndedReservation` — crew landed on Mun → reserved until PositiveInfinity (rescue linkage deferred, tests open-ended reservation behavior)
33. `FullScenario_RewindRecomputes` — design doc 9.11 scenario 4
34. `FullScenario_MultiCrewMission` — design doc 9.11 scenario 5
35. `FullScenario_StandInDiesInChain` — design doc 9.11 scenario 6
36. `FullScenario_OwnerDies_NoAutoReplacement` — design doc 9.11 scenario 7

### 9.3 Log Assertion Tests

37. `Recalculate_LogsReservationCount` — verify summary log line
38. `StandInGenerated_LogsNameAndTrait` — verify stand-in generation logged
39. `CommitBlocked_LogsReason` — verify "commit blocked" log with kerbal name
40. `DismissalBlocked_LogsReason` — verify "dismissal blocked" log

### 9.4 Serialization Tests

41. `CrewEndStates_RoundTrip` — save + load CrewEndStates ConfigNode
42. `KerbalSlots_RoundTrip` — save + load KERBAL_SLOTS ConfigNode
43. `Migration_OldCrewReplacements_LoadsAsChainDepth0` — backward compat with existing CREW_REPLACEMENTS

---

## 10. Files Modified/Created

| File | Change | Phase |
|------|--------|-------|
| `Source/Parsek/KerbalEndState.cs` | **New** — enum | A |
| `Source/Parsek/KerbalsModule.cs` | **New** — core module (~400 lines) | A |
| `Source/Parsek/Patches/KerbalDismissalPatch.cs` | **New** — Harmony prefix | A |
| `Source/Parsek/Recording.cs` | Add `CrewEndStates` field | A |
| `Source/Parsek/CrewReservationManager.cs` | Extend with chain serialization, delegate to KerbalsModule | A |
| `Source/Parsek/ParsekScenario.cs` | Replace `ReserveSnapshotCrew()` calls with `KerbalsModule.Recalculate()` | A |
| `Source/Parsek/MergeDialog.cs` | Call `KerbalsModule.Recalculate()` after commit | A |
| `Source/Parsek/ParsekFlight.cs` | Replace ReserveSnapshotCrew calls, add PopulateCrewEndStates | A |
| `Source/Parsek/RecordingStore.cs` | Add CrewEndStates to serialization | A |
| `Source/Parsek/ChainSegmentManager.cs` | Add RecalculateAndApply after chain commit (line 414) | A |
| `Source/Parsek.Tests/KerbalEndStateTests.cs` | **New** — unit tests | A |
| `Source/Parsek.Tests/KerbalReservationTests.cs` | **New** — reservation + chain tests | A |
| `Source/Parsek.Tests/KerbalRecalculationIntegrationTests.cs` | **New** — integration tests | A |

---

## 11. Implementation Order (Phase A)

1. Create `KerbalEndState.cs` — enum with explicit int values
2. Create `KerbalsModule.cs` — empty class with `Recalculate()` signature, `IsKerbalAvailable()`, `IsManaged()`
3. Add `CrewEndStates` field to `Recording.cs` + serialization in `RecordingStore`
4. Implement `InferCrewEndState()` as pure static method on `KerbalsModule`
5. Write `KerbalEndStateTests.cs` (tests 1-6)
6. Implement reservation computation in `Recalculate()` (walk recordings, build reservations)
7. Write `KerbalReservationTests.cs` (tests 7-14)
8. Implement chain computation in `Recalculate()` (build slots, generate stand-in actions)
9. Write `KerbalChainTests.cs` (tests 15-25)
10. Implement `ApplyToRoster()` — create stand-ins, delete unused, mark retired
11. Create `Patches/KerbalDismissalPatch.cs`
12. Write `KerbalDismissalTests.cs` (tests 26-29)
13. Add `KERBAL_SLOTS` serialization to `CrewReservationManager`
14. Write serialization tests (tests 41-43)
15. Integrate: replace `ReserveSnapshotCrew()` calls in ParsekScenario (lines 507, 534, 686) and MergeDialog (lines 126, 396, 810) with `KerbalsModule.Recalculate()` + `ApplyToRoster()`
16. Write integration tests (tests 30-36)
17. Write log assertion tests (tests 37-40)
18. Run `dotnet build` and `dotnet test` — all 3374 + ~43 new tests pass

---

## 12. What This Phase Does NOT Do

- **No funds/hiring costs** — sandbox mode, all kerbals free (Phase C)
- **No XP banking** — no flight log manipulation (Phase C)
- **No STRANDED detection** — Landed on remote body = Landed, not distinct (future)
- **No rescue mechanics** — no recording-to-recording rescue linkage (D6 in deferred)
- **No ledger integration** — standalone module, no game actions ledger dependency
- **No KSC spending interception** — no Harmony patch for kerbal hire blocking (Phase C)
- **No per-crew death events during recording** — relies on vessel TerminalState + roster status inference (future: emit `SegmentEventType.CrewLost`)
