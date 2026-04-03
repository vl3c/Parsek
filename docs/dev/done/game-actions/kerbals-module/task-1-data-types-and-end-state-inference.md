# Kerbals Task 1: Data Types and End State Inference

**Parent doc:** `docs/dev/plans/game-actions-kerbals-implementation-design.md`
**Scope:** New enum, new field on Recording, pure inference method, serialization, tests.
**No behavior changes.** No KSP roster mutation. No commit/rewind flow changes.

**Depends on:** Nothing.
**Enables:** Task 2 (reservation computation).
**Done when:** All types compile, serialization round-trips pass, inference tests pass.

---

## 1. New File: `Source/Parsek/KerbalEndState.cs`

```csharp
namespace Parsek
{
    /// <summary>
    /// Per-crew end state for a recording. Maps from vessel TerminalState
    /// and KSP roster status. Explicit int values for stable serialization.
    /// </summary>
    public enum KerbalEndState
    {
        Recovered  = 0,
        Landed     = 1,
        Orbiting   = 2,
        Dead       = 3,
        MIA        = 4
    }
}
```

---

## 2. New Field on `Recording.cs`

Add after `AntennaSpecs` (line ~92):

```csharp
/// <summary>
/// Per-crew end states for this recording. Keys are kerbal names.
/// Populated at commit time by PopulateCrewEndStates.
/// Serialized in RECORDING ConfigNode as CREW_END_STATES.
/// </summary>
public Dictionary<string, KerbalEndState> CrewEndStates;
```

Copy in `ApplyPersistenceArtifactsFrom` (after AntennaSpecs copy, line ~213):

```csharp
if (source.CrewEndStates != null)
    CrewEndStates = new Dictionary<string, KerbalEndState>(source.CrewEndStates);
```

---

## 3. Pure Static Method: `InferCrewEndState`

Add to a new `KerbalsModule.cs` file (this task creates the file with just this method — Task 2 adds reservation logic):

```csharp
namespace Parsek
{
    internal static class KerbalsModule
    {
        /// <summary>
        /// Infer a crew member's end state from the vessel's terminal state
        /// and the crew member's KSP roster status. Pure — no KSP state access.
        /// Roster status takes priority over vessel state (Dead crew on a
        /// Landed vessel is still Dead).
        /// </summary>
        internal static KerbalEndState InferCrewEndState(
            TerminalState? vesselState,
            ProtoCrewMember.RosterStatus rosterStatus)
        {
            // Roster status wins — KSP sets this regardless of vessel state
            if (rosterStatus == ProtoCrewMember.RosterStatus.Dead)
            {
                ParsekLog.Verbose("Kerbals",
                    "InferCrewEndState: roster status Dead → Dead");
                return KerbalEndState.Dead;
            }
            if (rosterStatus == ProtoCrewMember.RosterStatus.Missing)
            {
                ParsekLog.Verbose("Kerbals",
                    "InferCrewEndState: roster status Missing → MIA");
                return KerbalEndState.MIA;
            }

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
                    // Crew merged into survivor vessel — alive, will appear
                    // in survivor recording
                    return KerbalEndState.Recovered;
                default:
                    ParsekLog.Verbose("Kerbals",
                        $"InferCrewEndState: null/unknown terminal state → Orbiting (conservative)");
                    return KerbalEndState.Orbiting;
            }
        }

        /// <summary>
        /// Populate CrewEndStates on a recording from its VesselSnapshot crew
        /// and vessel TerminalState. Called at commit time.
        /// In tests (no KSP runtime), pass crewStatuses explicitly.
        /// In production, reads roster status from HighLogic.CurrentGame.CrewRoster.
        /// </summary>
        internal static void PopulateCrewEndStates(Recording rec)
        {
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(rec.VesselSnapshot);
            if (crew.Count == 0)
            {
                ParsekLog.Verbose("Kerbals",
                    $"PopulateCrewEndStates: no crew in '{rec.VesselName}' — skipping");
                return;
            }

            rec.CrewEndStates = new Dictionary<string, KerbalEndState>(crew.Count);
            var roster = HighLogic.CurrentGame?.CrewRoster;

            for (int i = 0; i < crew.Count; i++)
            {
                var status = ProtoCrewMember.RosterStatus.Available; // default if no roster
                if (roster != null)
                {
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == crew[i])
                        {
                            status = pcm.rosterStatus;
                            break;
                        }
                    }
                }

                var endState = InferCrewEndState(rec.TerminalStateValue, status);
                rec.CrewEndStates[crew[i]] = endState;

                ParsekLog.Info("Kerbals",
                    $"Crew end state: '{crew[i]}' → {endState} " +
                    $"(vessel={rec.TerminalStateValue}, roster={status})");
            }
        }

        /// <summary>
        /// Test-friendly overload: populate CrewEndStates with explicit statuses.
        /// </summary>
        internal static void PopulateCrewEndStates(
            Recording rec,
            Dictionary<string, ProtoCrewMember.RosterStatus> crewStatuses)
        {
            var crew = CrewReservationManager.ExtractCrewFromSnapshot(rec.VesselSnapshot);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>(crew.Count);

            for (int i = 0; i < crew.Count; i++)
            {
                ProtoCrewMember.RosterStatus status;
                if (!crewStatuses.TryGetValue(crew[i], out status))
                    status = ProtoCrewMember.RosterStatus.Available;

                rec.CrewEndStates[crew[i]] = InferCrewEndState(rec.TerminalStateValue, status);
            }
        }
    }
}
```

---

## 4. Serialization in `RecordingStore.cs`

**Save** — in the method that writes RECORDING ConfigNode (search for `TerminalStateValue` serialization and add after):

```csharp
if (rec.CrewEndStates != null && rec.CrewEndStates.Count > 0)
{
    ConfigNode cesNode = recNode.AddNode("CREW_END_STATES");
    foreach (var kvp in rec.CrewEndStates)
    {
        cesNode.AddValue("crew", kvp.Key);
        cesNode.AddValue("state", (int)kvp.Value);
    }
}
```

**Load** — in the method that reads RECORDING ConfigNode:

```csharp
ConfigNode cesNode = recNode.GetNode("CREW_END_STATES");
if (cesNode != null)
{
    string[] crews = cesNode.GetValues("crew");
    string[] states = cesNode.GetValues("state");
    int count = Math.Min(crews.Length, states.Length);
    rec.CrewEndStates = new Dictionary<string, KerbalEndState>(count);
    for (int i = 0; i < count; i++)
    {
        int stateVal;
        if (int.TryParse(states[i], out stateVal))
            rec.CrewEndStates[crews[i]] = (KerbalEndState)stateVal;
    }
}
```

---

## 5. Tests: `Source/Parsek.Tests/KerbalEndStateTests.cs`

```csharp
using Xunit;
using System.Collections.Generic;

namespace Parsek.Tests
{
    public class KerbalEndStateTests
    {
        // --- InferCrewEndState ---

        [Fact]
        public void InferCrewEndState_Recovered_ReturnsRecovered()
        {
            // Fails if recovered crew treated as stranded → wrong reservation duration
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Recovered, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Recovered, result);
        }

        [Fact]
        public void InferCrewEndState_Destroyed_ReturnsDead()
        {
            // Fails if destroyed crew treated as alive → duplicate kerbal possible
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Destroyed, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Dead, result);
        }

        [Fact]
        public void InferCrewEndState_DeadRosterStatus_OverridesVesselState()
        {
            // Fails if roster status not checked first → Dead crew on Landed
            // vessel misclassified as alive
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Landed, ProtoCrewMember.RosterStatus.Dead);
            Assert.Equal(KerbalEndState.Dead, result);
        }

        [Fact]
        public void InferCrewEndState_MissingRosterStatus_ReturnsMIA()
        {
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Orbiting, ProtoCrewMember.RosterStatus.Missing);
            Assert.Equal(KerbalEndState.MIA, result);
        }

        [Fact]
        public void InferCrewEndState_Landed_ReturnsLanded()
        {
            // Fails if landed crew treated as recovered → premature slot reclaim
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Landed, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Landed, result);
        }

        [Fact]
        public void InferCrewEndState_Splashed_ReturnsLanded()
        {
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Splashed, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Landed, result);
        }

        [Fact]
        public void InferCrewEndState_NullTerminalState_ReturnsOrbiting()
        {
            // Fails if null causes exception instead of conservative default
            var result = KerbalsModule.InferCrewEndState(
                null, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Orbiting, result);
        }

        [Fact]
        public void InferCrewEndState_Docked_ReturnsRecovered()
        {
            // Fails if docked crew treated as orbiting → wrong reservation
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Docked, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Recovered, result);
        }

        [Fact]
        public void InferCrewEndState_Boarded_ReturnsRecovered()
        {
            var result = KerbalsModule.InferCrewEndState(
                TerminalState.Boarded, ProtoCrewMember.RosterStatus.Available);
            Assert.Equal(KerbalEndState.Recovered, result);
        }

        // --- Serialization ---

        [Fact]
        public void CrewEndStates_RoundTrip()
        {
            // Fails if serialization loses data → wrong reservation on load
            var original = new Dictionary<string, KerbalEndState>
            {
                { "Jebediah Kerman", KerbalEndState.Recovered },
                { "Bill Kerman", KerbalEndState.Dead },
                { "Val Kerman", KerbalEndState.Landed }
            };

            var node = new ConfigNode("CREW_END_STATES");
            foreach (var kvp in original)
            {
                node.AddValue("crew", kvp.Key);
                node.AddValue("state", (int)kvp.Value);
            }

            string[] crews = node.GetValues("crew");
            string[] states = node.GetValues("state");
            var loaded = new Dictionary<string, KerbalEndState>();
            int count = System.Math.Min(crews.Length, states.Length);
            for (int i = 0; i < count; i++)
            {
                int stateVal;
                if (int.TryParse(states[i], out stateVal))
                    loaded[crews[i]] = (KerbalEndState)stateVal;
            }

            Assert.Equal(original.Count, loaded.Count);
            foreach (var kvp in original)
                Assert.Equal(kvp.Value, loaded[kvp.Key]);
        }

        // --- PopulateCrewEndStates (test-friendly overload) ---

        [Fact]
        public void PopulateCrewEndStates_MultiCrew_AllInferred()
        {
            // Fails if any crew member skipped → missing reservation
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jeb");
            part.AddValue("crew", "Bill");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Recovered
            };

            var statuses = new Dictionary<string, ProtoCrewMember.RosterStatus>
            {
                { "Jeb", ProtoCrewMember.RosterStatus.Available },
                { "Bill", ProtoCrewMember.RosterStatus.Available }
            };

            KerbalsModule.PopulateCrewEndStates(rec, statuses);

            Assert.NotNull(rec.CrewEndStates);
            Assert.Equal(2, rec.CrewEndStates.Count);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Bill"]);
        }

        [Fact]
        public void PopulateCrewEndStates_NoCrew_SkipsGracefully()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Orbiting
            };

            KerbalsModule.PopulateCrewEndStates(rec, new Dictionary<string, ProtoCrewMember.RosterStatus>());

            Assert.Null(rec.CrewEndStates); // no crew → not populated
        }

        [Fact]
        public void PopulateCrewEndStates_DeadCrewOnLandedVessel()
        {
            // Fails if roster status not checked → dead crew treated as landed
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jeb");
            part.AddValue("crew", "Bill");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed
            };

            var statuses = new Dictionary<string, ProtoCrewMember.RosterStatus>
            {
                { "Jeb", ProtoCrewMember.RosterStatus.Dead },
                { "Bill", ProtoCrewMember.RosterStatus.Available }
            };

            KerbalsModule.PopulateCrewEndStates(rec, statuses);

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Landed, rec.CrewEndStates["Bill"]);
        }
    }
}
```

---

## 6. Implementation Order

1. Create `Source/Parsek/KerbalEndState.cs` — the enum
2. Create `Source/Parsek/KerbalsModule.cs` — with `InferCrewEndState` and `PopulateCrewEndStates` (both overloads)
3. Add `CrewEndStates` field to `Recording.cs` + copy in `ApplyPersistenceArtifactsFrom`
4. Add serialization in `RecordingStore.cs` (save/load CREW_END_STATES)
5. Create `Source/Parsek.Tests/KerbalEndStateTests.cs` — all 12 tests
6. `dotnet build` + `dotnet test` — all 3374 + 12 new tests pass

## 7. Files Modified

| File | Change |
|------|--------|
| `Source/Parsek/KerbalEndState.cs` | **New** — 12 lines |
| `Source/Parsek/KerbalsModule.cs` | **New** — ~100 lines (inference + populate) |
| `Source/Parsek/Recording.cs` | Add `CrewEndStates` field + copy in `ApplyPersistenceArtifactsFrom` |
| `Source/Parsek/RecordingStore.cs` | Add CREW_END_STATES serialization (save + load) |
| `Source/Parsek.Tests/KerbalEndStateTests.cs` | **New** — ~180 lines, 12 tests |
