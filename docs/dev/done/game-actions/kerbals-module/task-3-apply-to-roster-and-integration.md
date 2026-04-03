# Kerbals Task 3: ApplyToRoster, Integration, and Dismissal Protection

**Parent doc:** `docs/dev/plans/game-actions-kerbals-implementation-design.md`
**Scope:** KSP roster mutation, commit/rewind flow integration, Harmony dismissal patch.
**This is the task that connects the pure computation (Tasks 1-2) to the live game.**

**Depends on:** Tasks 1 and 2.
**Enables:** In-game testing.
**Done when:** Crew reservation works end-to-end in sandbox mode. Existing 3374 tests + new tests pass. Manual in-game verification succeeds.

---

## 1. `ApplyToRoster()` Method

Add to `KerbalsModule.cs`:

```csharp
/// <summary>
/// Apply derived kerbal state to the KSP roster. Creates stand-ins,
/// removes unused displaced stand-ins, sets roster statuses, and
/// populates crewReplacements dict for SwapReservedCrewInFlight.
///
/// Must be called AFTER Recalculate().
/// Wraps all mutations in SuppressCrewEvents.
/// </summary>
internal static void ApplyToRoster(KerbalRoster roster)
{
    if (roster == null)
    {
        ParsekLog.Verbose("Kerbals", "ApplyToRoster: no roster — skipping");
        return;
    }

    GameStateRecorder.SuppressCrewEvents = true;
    try
    {
        // Step 1: Create missing stand-ins
        foreach (var kvp in slots)
        {
            var slot = kvp.Value;
            for (int i = 0; i < slot.Chain.Count; i++)
            {
                if (slot.Chain[i] != null)
                {
                    // Verify stand-in still exists in roster
                    if (FindInRoster(roster, slot.Chain[i]) == null)
                    {
                        // Stand-in was removed (e.g., KSP cleanup) — recreate
                        var created = CreateStandIn(roster, slot.OwnerTrait, slot.Chain[i]);
                        if (created == null)
                            ParsekLog.Warn("Kerbals",
                                $"Failed to recreate stand-in '{slot.Chain[i]}'");
                    }
                    continue;
                }

                // Null entry = pending generation (new depth from Recalculate)
                ProtoCrewMember newStandIn = roster.GetNewKerbal(
                    ProtoCrewMember.KerbalType.Crew);
                if (newStandIn != null)
                {
                    KerbalRoster.SetExperienceTrait(newStandIn, slot.OwnerTrait);
                    slot.Chain[i] = newStandIn.name;
                    ParsekLog.Info("Kerbals",
                        $"Stand-in generated: '{newStandIn.name}' ({slot.OwnerTrait}) " +
                        $"for slot '{slot.OwnerName}' depth {i}");
                }
                else
                {
                    ParsekLog.Warn("Kerbals",
                        $"Failed to generate stand-in for slot '{slot.OwnerName}' depth {i}");
                }
            }
        }

        // Step 2: Remove unused displaced stand-ins from roster
        foreach (var kvp in slots)
        {
            var slot = kvp.Value;
            bool ownerFree = !reservations.ContainsKey(slot.OwnerName)
                && !slot.OwnerPermanentlyGone;

            if (!ownerFree) continue; // owner still reserved — chain still needed

            // Owner is free. Displace all chain entries.
            for (int i = slot.Chain.Count - 1; i >= 0; i--)
            {
                string standIn = slot.Chain[i];
                if (standIn == null) continue;

                bool usedInRecording = IsKerbalInAnyRecording(standIn);
                if (usedInRecording)
                {
                    // Retired — keep in roster but mark Assigned (unassignable)
                    ParsekLog.Info("Kerbals",
                        $"Stand-in '{standIn}' displaced → retired (used in recording)");
                }
                else
                {
                    // Unused — remove from roster entirely
                    var pcm = FindInRoster(roster, standIn);
                    if (pcm != null && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available)
                    {
                        roster.Remove(pcm);
                        ParsekLog.Info("Kerbals",
                            $"Stand-in '{standIn}' displaced → deleted (unused)");
                    }
                }
            }

            // Clear the chain — owner has reclaimed
            slot.Chain.Clear();
        }

        // Step 3: Set roster statuses and populate crewReplacements bridge
        CrewReservationManager.ClearReplacementsInternal();

        foreach (var kvp in reservations)
        {
            string kerbalName = kvp.Key;
            var pcm = FindInRoster(roster, kerbalName);
            if (pcm != null)
            {
                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
            }

            // Bridge to SwapReservedCrewInFlight: map reserved → active occupant
            string occupant = GetActiveOccupant(kerbalName);
            if (occupant != null)
            {
                // Use the internal crewReplacements dict on CrewReservationManager
                // This is the bridge — SwapReservedCrewInFlight reads from this dict
                CrewReservationManager.SetReplacement(kerbalName, occupant);
            }
        }

        // Step 4: Set retired kerbals to Assigned (unassignable)
        foreach (string retired in retiredKerbals)
        {
            var pcm = FindInRoster(roster, retired);
            if (pcm != null)
                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
        }

        ParsekLog.Info("Kerbals",
            $"ApplyToRoster complete: {slots.Count} slots, " +
            $"{retiredKerbals.Count} retired, " +
            $"{reservations.Count} reserved");
    }
    finally
    {
        GameStateRecorder.SuppressCrewEvents = false;
    }
}

/// <summary>
/// Combined recalculate + apply for convenience.
/// The standard call at every commit/rewind point.
/// </summary>
internal static void RecalculateAndApply()
{
    var roster = HighLogic.CurrentGame?.CrewRoster;
    if (roster == null)
    {
        ParsekLog.Verbose("Kerbals", "RecalculateAndApply: no roster — skipping");
        return;
    }
    Recalculate();
    ApplyToRoster(roster);
}

private static ProtoCrewMember FindInRoster(KerbalRoster roster, string name)
{
    foreach (ProtoCrewMember pcm in roster.Crew)
    {
        if (pcm.name == name) return pcm;
    }
    return null;
}

private static ProtoCrewMember CreateStandIn(
    KerbalRoster roster, string trait, string existingName)
{
    var pcm = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
    if (pcm != null)
    {
        pcm.ChangeName(existingName);
        KerbalRoster.SetExperienceTrait(pcm, trait);
    }
    return pcm;
}
```

---

## 2. Bridge Method on `CrewReservationManager`

Add to `CrewReservationManager.cs`:

```csharp
/// <summary>
/// Set a crew replacement mapping. Called by KerbalsModule.ApplyToRoster
/// to bridge derived state to SwapReservedCrewInFlight.
/// </summary>
internal static void SetReplacement(string originalName, string replacementName)
{
    crewReplacements[originalName] = replacementName;
}

/// <summary>
/// Clear all replacements without roster access. For KerbalsModule use.
/// </summary>
internal static void ClearReplacementsInternal()
{
    crewReplacements.Clear();
}
```

---

## 3. Harmony Patch: Dismissal Protection

**New file:** `Source/Parsek/Patches/KerbalDismissalPatch.cs`

```csharp
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents dismissal of Parsek-managed kerbals (reserved, stand-ins, retired)
    /// from the Astronaut Complex. Same pattern as TechResearchPatch.
    /// </summary>
    [HarmonyPatch(typeof(KerbalRoster), nameof(KerbalRoster.Remove))]
    internal static class KerbalDismissalPatch
    {
        static bool Prefix(ProtoCrewMember crew)
        {
            // Allow Parsek's own cleanup calls
            if (GameStateRecorder.SuppressCrewEvents) return true;
            if (GameStateRecorder.IsReplayingActions) return true;

            if (KerbalsModule.IsManaged(crew.name))
            {
                ParsekLog.Info("KerbalDismissal",
                    $"Blocked dismissal of '{crew.name}' — managed by Parsek");
                // TODO Phase B: show popup dialog like CommittedActionDialog
                return false;
            }
            return true;
        }
    }
}
```

Register in `ParsekHarmony.cs` — the existing Harmony patcher auto-discovers `[HarmonyPatch]` attributes, so no explicit registration needed if using `PatchAll()`.

---

## 4. Integration: Replace `ReserveSnapshotCrew` Calls

**Search pattern:** `CrewReservationManager.ReserveSnapshotCrew()`

**Replace with:** `KerbalsModule.RecalculateAndApply()`

**All call sites:**

| File | Method | Action |
|------|--------|--------|
| `MergeDialog.cs:126` | After standalone commit | Replace |
| `MergeDialog.cs:188` | After defer-spawn commit | Replace |
| `MergeDialog.cs:396` | After tree commit (single) | Replace |
| `MergeDialog.cs:810` | After tree commit (multi) | Replace |
| `ParsekFlight.cs:3602` | After EVA child auto-commit | Replace |
| `ParsekScenario.cs:507` | After revert scene load | Replace |
| `ParsekScenario.cs:534` | After initial load | Replace |
| `ParsekScenario.cs:686` | After rewind | Replace |

**Call sites that currently LACK crew handling (add `RecalculateAndApply`):**

| File | Line(s) | Method | Action |
|------|---------|--------|--------|
| `ParsekFlight.cs` | 1190 | Auto-merge tree on destruction | Add |
| `ParsekFlight.cs` | 1931 | Vessel destroyed during split | Add |
| `ParsekFlight.cs` | 4629 | CommitFlight button | Add (after existing `ReserveCrewForLeaves`) |
| `ParsekFlight.cs` | 7756 | CommitOrShowDialog auto-merge | Add |
| `ParsekScenario.cs` | 37, 44 | Safety-net OnSave commits | Add |
| `ParsekScenario.cs` | 489, 495 | Auto-commit ghost-only/tree | Add |
| `ParsekScenario.cs` | 593, 606 | Outside-Flight autocommit | Add |
| `ParsekScenario.cs` | 813 | EVA child auto-commit in deferred merge | Add |
| `ChainSegmentManager.cs` | 414 | Chain segment commit | Add |

**ParsekFlight.ReserveCrewForLeaves (line 4694):** This calls `ReserveCrewIn` for individual tree leaves. Replace with `RecalculateAndApply()` after `CommitTree` — the recalculation handles all leaves.

**`PopulateCrewEndStates` call:** Add to the `RecalculateAndApply()` method itself, BEFORE `Recalculate()`. For each committed recording that has a null `CrewEndStates` and a non-null `VesselSnapshot`, call `PopulateCrewEndStates(rec)`:

```csharp
internal static void RecalculateAndApply()
{
    var roster = HighLogic.CurrentGame?.CrewRoster;
    if (roster == null) return;

    // Populate end states on any recording that hasn't been populated yet
    for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
    {
        var rec = RecordingStore.CommittedRecordings[i];
        if (rec.CrewEndStates == null && rec.VesselSnapshot != null)
            PopulateCrewEndStates(rec);
    }

    Recalculate();
    ApplyToRoster(roster);
}
```

This ensures end states are populated on first recalculation after commit, and also handles legacy recordings loaded from saves (they have no `CrewEndStates` yet).

---

## 5. Integration: Serialization Hooks

In `ParsekScenario.OnSave`:

```csharp
// After existing CrewReservationManager.SaveCrewReplacements(node):
KerbalsModule.SaveSlots(node);
// Keep writing CREW_REPLACEMENTS for backward compat (one version window)
CrewReservationManager.SaveCrewReplacements(node);
```

In `ParsekScenario.OnLoad`:

```csharp
// After existing CrewReservationManager.LoadCrewReplacements(node):
KerbalsModule.LoadSlots(node);
```

In rewind path (`HandleRewindOnLoad`): skip `LoadSlots` (same as existing `LoadCrewReplacements` skip at line 214 — in-memory state is authoritative during rewind). Add the guard:

```csharp
// In OnLoad, around line 214-218:
if (!RecordingStore.IsRewinding)
{
    CrewReservationManager.LoadCrewReplacements(node);
    KerbalsModule.LoadSlots(node);  // NEW: load alongside crew replacements
}
```

---

## 6. Tests

**`Source/Parsek.Tests/KerbalDismissalTests.cs`:**

```csharp
[Fact]
public void IsManaged_ReservedKerbal_ReturnsTrue()
    // Fails if dismissal allowed for reserved crew → recording reference broken

[Fact]
public void IsManaged_RetiredKerbal_ReturnsTrue()
    // Fails if retired dismissed → recording reference broken

[Fact]
public void IsManaged_UnmanagedKerbal_ReturnsFalse()
    // Fails if unrelated kerbals blocked → player can't manage roster
```

**Integration tests (manual in-game):**

1. Launch with Jeb+Bill, record, revert, commit → Jeb+Bill reserved, replacements on pad
2. Launch again with replacements, record, revert, commit → chain depth 2
3. Fast-forward past first recording endUT → Jeb reclaims, stand-in deleted or retired
4. Try to dismiss reserved kerbal in Astronaut Complex → blocked
5. Try to dismiss unrelated kerbal → allowed

---

## 7. Implementation Order

1. Add `SetReplacement`/`ClearReplacementsInternal` to `CrewReservationManager.cs`
2. Implement `ApplyToRoster()` on `KerbalsModule.cs`
3. Implement `RecalculateAndApply()` convenience method
4. Implement `FindInRoster()` / `CreateStandIn()` helpers
5. Create `Patches/KerbalDismissalPatch.cs`
6. Replace all `ReserveSnapshotCrew()` calls with `RecalculateAndApply()` (8 sites)
7. Add `RecalculateAndApply()` to commit paths that lack crew handling (6 sites)
8. Add serialization hooks in `ParsekScenario.OnSave`/`OnLoad`
9. Replace `ParsekFlight.ReserveCrewForLeaves` with `RecalculateAndApply()` after `CommitTree`
10. Create `KerbalDismissalTests.cs`
11. `dotnet build` + `dotnet test`
12. Manual in-game testing in sandbox mode

## 8. Files Modified

| File | Change |
|------|--------|
| `Source/Parsek/KerbalsModule.cs` | Add ApplyToRoster, RecalculateAndApply, helpers (~200 lines) |
| `Source/Parsek/CrewReservationManager.cs` | Add SetReplacement, ClearReplacementsInternal |
| `Source/Parsek/Patches/KerbalDismissalPatch.cs` | **New** — Harmony prefix (~25 lines) |
| `Source/Parsek/ParsekScenario.cs` | Replace ReserveSnapshotCrew calls, add serialization hooks |
| `Source/Parsek/MergeDialog.cs` | Replace ReserveSnapshotCrew calls (4 sites) |
| `Source/Parsek/ParsekFlight.cs` | Replace ReserveSnapshotCrew, add missing crew handling |
| `Source/Parsek/ChainSegmentManager.cs` | Add RecalculateAndApply after chain commit |
| `Source/Parsek.Tests/KerbalDismissalTests.cs` | **New** — 3 tests |

## 9. What This Task Does NOT Do

- **No chain depth testing** — Task 2 covers chain computation tests
- **No end state inference** — Task 1 covers InferCrewEndState tests
- **No XP or hiring costs** — Phase C
- **No rescue linkage** — deferred
- **No UI changes** — future task
