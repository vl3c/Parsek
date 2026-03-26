# Kerbals Task 2: Reservation Computation and Chain Building

**Parent doc:** `docs/dev/plans/game-actions-kerbals-implementation-design.md`
**Scope:** Pure reservation computation, chain building, slot management. No KSP roster mutation yet (that's Task 3).

**Depends on:** Task 1 (KerbalEndState enum, InferCrewEndState, CrewEndStates on Recording).
**Enables:** Task 3 (ApplyToRoster, integration with commit/rewind flows).
**Done when:** Recalculation produces correct reservations and chains for all design doc scenarios, all tests pass.

---

## 1. Extend `KerbalsModule.cs` — Core Data Structures

Add to the existing file created in Task 1:

```csharp
// --- Derived state (recomputed on every Recalculate call) ---
private static Dictionary<string, KerbalReservation> reservations
    = new Dictionary<string, KerbalReservation>();
private static HashSet<string> retiredKerbals = new HashSet<string>();

// --- Persisted state (stand-in names survive recalculation) ---
private static Dictionary<string, KerbalSlot> slots
    = new Dictionary<string, KerbalSlot>();

/// <summary>
/// Per-kerbal reservation. Derived — never persisted directly.
/// </summary>
internal class KerbalReservation
{
    public string KerbalName;
    public double ReservedUntilUT;  // double.PositiveInfinity for permanent/open-ended
    public bool IsPermanent;        // Dead or MIA — never freed
}

/// <summary>
/// Per-slot replacement chain. Slot name = original kerbal.
/// Persisted in KERBAL_SLOTS ConfigNode for name stability.
/// </summary>
internal class KerbalSlot
{
    public string OwnerName;
    public string OwnerTrait;       // "Pilot" / "Engineer" / "Scientist"
    public bool OwnerPermanentlyGone;
    public List<string> Chain = new List<string>(); // stand-in names, ordered by depth
}

// Read-only access for tests
internal static IReadOnlyDictionary<string, KerbalReservation> Reservations => reservations;
internal static IReadOnlyDictionary<string, KerbalSlot> Slots => slots;
internal static IReadOnlyCollection<string> RetiredKerbals => retiredKerbals;
```

---

## 2. `Recalculate()` Method

```csharp
/// <summary>
/// Recompute all kerbal reservations and chain state from committed recordings.
/// Pure computation over recording data — no KSP roster access.
/// Call ApplyToRoster() afterward to mutate KSP state.
/// </summary>
internal static void Recalculate()
{
    // 1. Clear derived state. DO NOT clear slots (names persist).
    reservations.Clear();
    retiredKerbals.Clear();

    var recordings = RecordingStore.CommittedRecordings;

    // 2. Build reservations from all committed recordings
    for (int i = 0; i < recordings.Count; i++)
    {
        var rec = recordings[i];
        if (rec.LoopPlayback) continue;
        if (RecordingStore.IsChainFullyDisabled(rec.ChainId)) continue;

        var crew = CrewReservationManager.ExtractCrewFromSnapshot(rec.VesselSnapshot);
        if (crew.Count == 0) continue;

        for (int c = 0; c < crew.Count; c++)
        {
            string name = crew[c];
            KerbalEndState endState = KerbalEndState.Orbiting;
            if (rec.CrewEndStates != null)
                rec.CrewEndStates.TryGetValue(name, out endState);

            bool permanent = (endState == KerbalEndState.Dead || endState == KerbalEndState.MIA);
            double endUT = (endState == KerbalEndState.Recovered) ? rec.EndUT : double.PositiveInfinity;

            KerbalReservation existing;
            if (reservations.TryGetValue(name, out existing))
            {
                // Merge: take max endUT, permanent wins
                if (permanent) existing.IsPermanent = true;
                if (endUT > existing.ReservedUntilUT) existing.ReservedUntilUT = endUT;
                ParsekLog.Verbose("Kerbals",
                    $"Reservation extended: '{name}' endUT→{existing.ReservedUntilUT:F1} " +
                    $"(permanent={existing.IsPermanent})");
            }
            else
            {
                reservations[name] = new KerbalReservation
                {
                    KerbalName = name,
                    ReservedUntilUT = endUT,
                    IsPermanent = permanent
                };
                ParsekLog.Info("Kerbals",
                    $"Reservation: '{name}' UT=0→{(permanent ? "INDEFINITE" : endUT.ToString("F1"))} " +
                    $"({endState}), recording '{rec.RecordingId}'");
            }
        }
    }

    // 3. Build/update chains for temporary reservations
    foreach (var kvp in reservations)
    {
        if (kvp.Value.IsPermanent)
        {
            // Permanent: slot exits chain system. Mark owner as gone.
            KerbalSlot permanentSlot;
            if (slots.TryGetValue(kvp.Key, out permanentSlot))
                permanentSlot.OwnerPermanentlyGone = true;
            continue;
        }

        // Ensure slot exists
        KerbalSlot slot;
        if (!slots.TryGetValue(kvp.Key, out slot))
        {
            slot = new KerbalSlot
            {
                OwnerName = kvp.Key,
                OwnerTrait = FindTraitForKerbal(kvp.Key),
            };
            slots[kvp.Key] = slot;
        }

        // Walk chain: ensure each reserved level has a stand-in
        EnsureChainDepth(slot);
    }

    // 4. Identify retired stand-ins
    ComputeRetiredSet();

    // 5. Log summary
    ParsekLog.Info("Kerbals",
        $"Recalculation complete: {reservations.Count} reservations, " +
        $"{slots.Count} slots, {retiredKerbals.Count} retired");
}
```

---

## 3. Helper Methods

```csharp
/// <summary>
/// Ensure the chain has a stand-in at every depth where the occupant is reserved.
/// Stand-in names are reused from existing chain entries (deterministic).
/// New names are generated only when a new depth is needed.
/// </summary>
private static void EnsureChainDepth(KerbalSlot slot)
{
    // Start with the owner. If reserved, need a stand-in at depth 0.
    // If that stand-in is also reserved, need depth 1, etc.
    string currentOccupant = slot.OwnerName;
    int depth = 0;

    while (reservations.ContainsKey(currentOccupant))
    {
        if (depth >= slot.Chain.Count)
        {
            // Need a new stand-in at this depth — will be created by ApplyToRoster
            // For now, mark as needing generation (name = null)
            slot.Chain.Add(null); // placeholder — ApplyToRoster fills with real name
            ParsekLog.Info("Kerbals",
                $"Chain depth {depth} needed for slot '{slot.OwnerName}' — pending generation");
        }

        currentOccupant = slot.Chain[depth];
        if (currentOccupant == null) break; // pending generation, stop walking
        depth++;
    }
}

/// <summary>
/// Determine which stand-ins are retired (used in a recording but displaced).
/// A stand-in is displaced when its predecessor (owner or earlier stand-in) is free.
/// </summary>
private static void ComputeRetiredSet()
{
    foreach (var kvp in slots)
    {
        var slot = kvp.Value;
        bool predecessorFree = !reservations.ContainsKey(slot.OwnerName) && !slot.OwnerPermanentlyGone;

        for (int i = 0; i < slot.Chain.Count; i++)
        {
            string standIn = slot.Chain[i];
            if (standIn == null) continue;

            bool isReserved = reservations.ContainsKey(standIn);
            bool usedInRecording = IsKerbalInAnyRecording(standIn);

            if (predecessorFree && usedInRecording && !isReserved)
            {
                retiredKerbals.Add(standIn);
                ParsekLog.Info("Kerbals",
                    $"Retired: '{standIn}' (used in recording, displaced by predecessor)");
            }

            // Next predecessor is free only if this one is also free
            predecessorFree = predecessorFree || !isReserved;
        }
    }
}

/// <summary>
/// Check if a kerbal name appears in any committed recording's crew.
/// Used to determine UsedInRecording for chain entries.
/// </summary>
internal static bool IsKerbalInAnyRecording(string kerbalName)
{
    var recordings = RecordingStore.CommittedRecordings;
    for (int i = 0; i < recordings.Count; i++)
    {
        if (recordings[i].LoopPlayback) continue;
        if (RecordingStore.IsChainFullyDisabled(recordings[i].ChainId)) continue;
        var crew = CrewReservationManager.ExtractCrewFromSnapshot(recordings[i].VesselSnapshot);
        if (crew.Contains(kerbalName)) return true;
    }
    return false;
}

/// <summary>
/// Find the experience trait for a kerbal by scanning committed recording snapshots.
/// Falls back to "Pilot" if not found.
/// </summary>
private static string FindTraitForKerbal(string kerbalName)
{
    // In production, could read from KSP roster. In tests, scan snapshots.
    // For Phase A, default to reading from roster if available, else "Pilot"
    var roster = HighLogic.CurrentGame?.CrewRoster;
    if (roster != null)
    {
        foreach (ProtoCrewMember pcm in roster.Crew)
        {
            if (pcm.name == kerbalName)
                return pcm.experienceTrait?.TypeName ?? "Pilot";
        }
    }
    return "Pilot";
}

/// <summary>
/// Check if a kerbal is available for a new recording.
/// A kerbal is available if they are NOT in the reservations dict.
/// </summary>
internal static bool IsKerbalAvailable(string kerbalName)
{
    bool reserved = reservations.ContainsKey(kerbalName);
    ParsekLog.Info("Kerbals",
        $"Availability check: '{kerbalName}' → {(reserved ? "RESERVED" : "available")}");
    return !reserved;
}

/// <summary>
/// Check if a kerbal is managed by Parsek (reserved, active stand-in, or retired).
/// </summary>
internal static bool IsManaged(string kerbalName)
{
    if (reservations.ContainsKey(kerbalName)) return true;
    if (retiredKerbals.Contains(kerbalName)) return true;

    // Check if they're a stand-in in any chain
    foreach (var slot in slots.Values)
    {
        if (slot.Chain.Contains(kerbalName)) return true;
    }
    return false;
}

/// <summary>
/// Get the active occupant for a slot (the deepest non-reserved chain member,
/// or the owner if free).
/// </summary>
internal static string GetActiveOccupant(string slotOwnerName)
{
    if (!reservations.ContainsKey(slotOwnerName))
        return slotOwnerName; // owner is free

    KerbalSlot slot;
    if (!slots.TryGetValue(slotOwnerName, out slot))
        return null; // no slot — shouldn't happen

    // Walk chain from deepest to shallowest
    for (int i = slot.Chain.Count - 1; i >= 0; i--)
    {
        string standIn = slot.Chain[i];
        if (standIn != null && !reservations.ContainsKey(standIn))
            return standIn;
    }

    // All reserved — the deepest pending (null) entry is the active occupant
    // (will be generated by ApplyToRoster)
    return null;
}

/// <summary>
/// Reset all state for testing. Clears reservations, slots, and retired set.
/// </summary>
internal static void ResetForTesting()
{
    reservations.Clear();
    slots.Clear();
    retiredKerbals.Clear();
}
```

---

## 4. KERBAL_SLOTS Serialization

Add to `KerbalsModule.cs`:

```csharp
internal static void SaveSlots(ConfigNode parentNode)
{
    if (slots.Count == 0) return;

    ConfigNode slotsNode = parentNode.AddNode("KERBAL_SLOTS");
    foreach (var kvp in slots)
    {
        var slot = kvp.Value;
        ConfigNode slotNode = slotsNode.AddNode("SLOT");
        slotNode.AddValue("owner", slot.OwnerName);
        slotNode.AddValue("trait", slot.OwnerTrait);
        for (int i = 0; i < slot.Chain.Count; i++)
        {
            if (slot.Chain[i] != null)
            {
                ConfigNode entry = slotNode.AddNode("CHAIN_ENTRY");
                entry.AddValue("name", slot.Chain[i]);
            }
        }
    }
    ParsekLog.Info("Kerbals", $"Saved {slots.Count} kerbal slot(s)");
}

internal static void LoadSlots(ConfigNode parentNode)
{
    slots.Clear();

    // Try new format first
    ConfigNode slotsNode = parentNode.GetNode("KERBAL_SLOTS");
    if (slotsNode != null)
    {
        ConfigNode[] slotNodes = slotsNode.GetNodes("SLOT");
        for (int i = 0; i < slotNodes.Length; i++)
        {
            var slot = new KerbalSlot
            {
                OwnerName = slotNodes[i].GetValue("owner") ?? "",
                OwnerTrait = slotNodes[i].GetValue("trait") ?? "Pilot",
                Chain = new List<string>()
            };
            ConfigNode[] entries = slotNodes[i].GetNodes("CHAIN_ENTRY");
            for (int j = 0; j < entries.Length; j++)
            {
                string name = entries[j].GetValue("name");
                if (!string.IsNullOrEmpty(name))
                    slot.Chain.Add(name);
            }
            if (!string.IsNullOrEmpty(slot.OwnerName))
                slots[slot.OwnerName] = slot;
        }
        ParsekLog.Info("Kerbals", $"Loaded {slots.Count} kerbal slot(s) from KERBAL_SLOTS");
        return;
    }

    // Backward compat: migrate from CREW_REPLACEMENTS
    ConfigNode replacementsNode = parentNode.GetNode("CREW_REPLACEMENTS");
    if (replacementsNode != null)
    {
        ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
        for (int i = 0; i < entries.Length; i++)
        {
            string original = entries[i].GetValue("original");
            string replacement = entries[i].GetValue("replacement");
            if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(replacement))
            {
                // Check if this original is already a stand-in for someone else
                // (existing flat format can't represent chains, so treat each as depth 0)
                if (!slots.ContainsKey(original))
                {
                    slots[original] = new KerbalSlot
                    {
                        OwnerName = original,
                        OwnerTrait = "Pilot", // can't determine from old format
                        Chain = new List<string> { replacement }
                    };
                }
            }
        }
        ParsekLog.Info("Kerbals",
            $"Migrated {slots.Count} slot(s) from legacy CREW_REPLACEMENTS");
    }
}
```

---

## 5. Tests: `Source/Parsek.Tests/KerbalReservationTests.cs`

```csharp
using Xunit;
using System.Collections.Generic;

namespace Parsek.Tests
{
    [Collection("Sequential")] // touches KerbalsModule static state
    public class KerbalReservationTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalReservationTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            KerbalsModule.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            KerbalsModule.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        private Recording MakeRecording(string vesselName, string[] crew,
            TerminalState terminal, double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);

            var rec = new Recording
            {
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = terminal
            };

            // Set explicit UT (no points)
            rec.ExplicitStartUT = 0;
            rec.ExplicitEndUT = endUT;

            // Populate end states
            var statuses = new Dictionary<string, ProtoCrewMember.RosterStatus>();
            foreach (var c in crew)
            {
                statuses[c] = (terminal == TerminalState.Destroyed)
                    ? ProtoCrewMember.RosterStatus.Dead
                    : ProtoCrewMember.RosterStatus.Available;
            }
            KerbalsModule.PopulateCrewEndStates(rec, statuses);

            return rec;
        }

        [Fact]
        public void Recalculate_SingleRecording_ReservesAllCrew()
        {
            // Fails if crew extraction misses multi-crew → duplicate kerbal paradox
            var rec = MakeRecording("Ship", new[] { "Jeb", "Bill" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.False(KerbalsModule.IsKerbalAvailable("Jeb"));
            Assert.False(KerbalsModule.IsKerbalAvailable("Bill"));
            Assert.True(KerbalsModule.IsKerbalAvailable("Val")); // not in recording
        }

        [Fact]
        public void Recalculate_RecoveredCrew_TemporaryReservation()
        {
            // Fails if recovered uses PositiveInfinity → crew locked forever
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Equal(2000.0, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(KerbalsModule.Reservations["Jeb"].IsPermanent);
        }

        [Fact]
        public void Recalculate_DeadCrew_PermanentReservation()
        {
            // Fails if dead crew not marked permanent → stand-in generated for dead crew
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.Reservations["Jeb"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void Recalculate_MultipleRecordings_MaxEndUT()
        {
            // Fails if endUT not merged → crew freed too early between recordings
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Jeb" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddCommittedForTesting(recA);
            RecordingStore.AddCommittedForTesting(recB);

            KerbalsModule.Recalculate();

            Assert.Equal(3000.0, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void Recalculate_LandedOnRemoteBody_OpenEndedReservation()
        {
            // Fails if landed treated as recovered → crew freed prematurely
            var rec = MakeRecording("Lander", new[] { "Jeb" },
                TerminalState.Landed, 5000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(KerbalsModule.Reservations["Jeb"].IsPermanent); // temporary, not dead
        }

        [Fact]
        public void Recalculate_NoCrewRecording_NoReservations()
        {
            // Fails if probe snapshot triggers reservation
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART").AddValue("name", "probeCoreCube");
            var rec = new Recording
            {
                VesselName = "Probe",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Orbiting,
                ExplicitStartUT = 0, ExplicitEndUT = 1000
            };
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Empty(KerbalsModule.Reservations);
        }

        [Fact]
        public void Recalculate_SkipsLoopRecordings()
        {
            // Fails if loop recordings reserve crew → visual replays block crew
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.LoopPlayback = true;
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.IsKerbalAvailable("Jeb"));
        }

        [Fact]
        public void GetActiveOccupant_OwnerFree_ReturnsOwner()
        {
            // Owner not reserved → owner is active
            Assert.Equal("Jeb", KerbalsModule.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void IsManaged_ReservedKerbal_ReturnsTrue()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.IsManaged("Jeb"));
        }

        [Fact]
        public void IsManaged_UnmanagedKerbal_ReturnsFalse()
        {
            Assert.False(KerbalsModule.IsManaged("Val"));
        }

        // --- Serialization ---

        [Fact]
        public void KerbalSlots_RoundTrip()
        {
            // Fails if slot names lost on save/load → orphaned stand-ins
            var slot = new KerbalsModule.KerbalSlot
            {
                OwnerName = "Jeb",
                OwnerTrait = "Pilot",
                Chain = new List<string> { "Hanley", "Kirrim" }
            };

            // Manually add to slots for save
            KerbalsModule.ResetForTesting();
            // Use SaveSlots/LoadSlots via ConfigNode
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var e1 = slotNode.AddNode("CHAIN_ENTRY");
            e1.AddValue("name", "Hanley");
            var e2 = slotNode.AddNode("CHAIN_ENTRY");
            e2.AddValue("name", "Kirrim");

            KerbalsModule.LoadSlots(parent);

            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));
            Assert.Equal("Pilot", KerbalsModule.Slots["Jeb"].OwnerTrait);
            Assert.Equal(2, KerbalsModule.Slots["Jeb"].Chain.Count);
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
            Assert.Equal("Kirrim", KerbalsModule.Slots["Jeb"].Chain[1]);
        }

        [Fact]
        public void Migration_OldCrewReplacements_LoadsAsChainDepth0()
        {
            // Fails if old saves break → data loss on upgrade
            var parent = new ConfigNode("TEST");
            var crNode = parent.AddNode("CREW_REPLACEMENTS");
            var entry = crNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            entry.AddValue("replacement", "Hanley");

            KerbalsModule.LoadSlots(parent);

            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));
            Assert.Single(KerbalsModule.Slots["Jeb"].Chain);
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
        }

        // --- Log assertions ---

        [Fact]
        public void Recalculate_LogsReservationCount()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb", "Bill" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Contains(logLines, l =>
                l.Contains("[Kerbals]") && l.Contains("2 reservations"));
        }

        [Fact]
        public void CommitBlocked_LogsReason()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            KerbalsModule.IsKerbalAvailable("Jeb");

            Assert.Contains(logLines, l =>
                l.Contains("[Kerbals]") && l.Contains("'Jeb'") && l.Contains("RESERVED"));
        }
    }
}
```

---

## 6. Implementation Order

1. Add `KerbalReservation`, `KerbalSlot` classes and static state to `KerbalsModule.cs`
2. Add read-only accessors for tests
3. Implement `Recalculate()` — reservation building from recordings
4. Implement `EnsureChainDepth()` — chain walk with existing name reuse
5. Implement `ComputeRetiredSet()` — displaced used stand-ins
6. Implement `IsKerbalAvailable()`, `IsManaged()`, `GetActiveOccupant()`
7. Implement `IsKerbalInAnyRecording()` helper
8. Implement `FindTraitForKerbal()` helper
9. Implement `SaveSlots()`/`LoadSlots()` with backward compat migration
10. Implement `ResetForTesting()`
11. Add `RecordingStore.AddCommittedForTesting(rec)` helper if not already present
12. Create `KerbalReservationTests.cs` — all tests
13. `dotnet build` + `dotnet test`

## 7. Files Modified

| File | Change |
|------|--------|
| `Source/Parsek/KerbalsModule.cs` | Add reservation, chain, slot logic (~350 lines) |
| `Source/Parsek/RecordingStore.cs` | Add `AddCommittedForTesting` if not present |
| `Source/Parsek.Tests/KerbalReservationTests.cs` | **New** — ~250 lines, 14 tests |
