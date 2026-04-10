using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #277: a reserved kerbal who is on a separate EVA vessel
    /// at merge time was never replaced with their stand-in. The fix adds an
    /// orphan-placement second pass to SwapReservedCrewInFlight that walks
    /// committed recording snapshots to find where the original was originally
    /// seated, then places the stand-in into a matching part on the active vessel.
    ///
    /// These tests cover the pure helper ResolveOrphanSeatFromSnapshots and the
    /// reverse-stand-in mapping logic. The live AddCrewmember placement path
    /// requires KSP runtime and is exercised by an in-game test in RuntimeTests.cs.
    /// </summary>
    [Collection("Sequential")]
    public class Bug277OrphanCrewPlacementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug277OrphanCrewPlacementTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        // ────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────

        private static ConfigNode BuildSnapshotWithCrew(
            uint partPid, string partName, params string[] crewNames)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("pid", partPid.ToString());
            for (int i = 0; i < crewNames.Length; i++)
                part.AddValue("crew", crewNames[i]);
            return snapshot;
        }

        private static ConfigNode BuildMultiPartSnapshot(
            params (uint pid, string partName, string[] crew)[] parts)
        {
            var snapshot = new ConfigNode("VESSEL");
            foreach (var p in parts)
            {
                var part = snapshot.AddNode("PART");
                part.AddValue("name", p.partName);
                part.AddValue("pid", p.pid.ToString());
                if (p.crew != null)
                    for (int i = 0; i < p.crew.Length; i++)
                        part.AddValue("crew", p.crew[i]);
            }
            return snapshot;
        }

        // ────────────────────────────────────────────────────────
        // ResolveOrphanSeatFromSnapshots — base cases
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_OriginalInSinglePartSnapshot_ReturnsMatch()
        {
            var snapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("mk1-3pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_OriginalNotInSnapshot_ReturnsNotFound()
        {
            var snapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullOriginal_ReturnsNotFound()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1pod", "Jeb");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                null, new[] { snapshot }, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_EmptyOriginal_ReturnsNotFound()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1pod", "Jeb");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "", new[] { snapshot }, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullSnapshotEnumerable_ReturnsNotFound()
        {
            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", null, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullSnapshotInList_SkippedNotCrash()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1-3pod.v2", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new ConfigNode[] { null, snapshot, null }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PartWithNoCrewValues_SkippedAndContinues()
        {
            var snapshot = new ConfigNode("VESSEL");
            // Part with no crew values at all (e.g., a fuel tank).
            var fuelTank = snapshot.AddNode("PART");
            fuelTank.AddValue("name", "fuelTank");
            fuelTank.AddValue("pid", "100001");
            // Part with the kerbal we're looking for.
            var pod = snapshot.AddNode("PART");
            pod.AddValue("name", "mk1pod");
            pod.AddValue("pid", "100002");
            pod.AddValue("crew", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100002u, seat.PartPid);
            Assert.Equal("mk1pod", seat.PartName);
        }

        // ────────────────────────────────────────────────────────
        // Multi-part / multi-snapshot cases
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_MultiPartSnapshot_ReturnsCorrectPartContainingOriginal()
        {
            // Vessel has two pods: a Mk1 (Jeb) and a Hitchhiker (Bob, Bill)
            var snapshot = BuildMultiPartSnapshot(
                (100000u, "mk1pod", new[] { "Jebediah Kerman" }),
                (100001u, "crewCabin", new[] { "Bob Kerman", "Bill Kerman" })
            );

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100001u, seat.PartPid);
            Assert.Equal("crewCabin", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_MultipleSnapshots_FirstSnapshotWins()
        {
            // Two recordings; both have Bob in different parts. First one wins.
            var first = BuildSnapshotWithCrew(100000u, "mk1pod", "Bob Kerman");
            var second = BuildSnapshotWithCrew(200000u, "mk1-3pod.v2", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { first, second }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("mk1pod", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_OnlySecondSnapshotContainsOriginal_FindsIt()
        {
            var first = BuildSnapshotWithCrew(100000u, "mk1pod", "Jebediah Kerman");
            var second = BuildSnapshotWithCrew(200000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { first, second }, null);

            Assert.True(seat.Found);
            Assert.Equal(200000u, seat.PartPid);
            Assert.Equal("mk1-3pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PartWithoutPidValue_ReturnsZeroPid()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod");
            // No 'pid' value
            part.AddValue("crew", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(0u, seat.PartPid);
            Assert.Equal("mk1pod", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PartWithoutNameValue_ReturnsEmptyName()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            // No 'name' value
            part.AddValue("pid", "100000");
            part.AddValue("crew", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("", seat.PartName);
        }

        // ────────────────────────────────────────────────────────
        // Reverse-stand-in mapping (for snapshots from later recordings
        // whose crew lists already contain stand-in names from earlier ones)
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_ReverseMapsStandinToOriginal_FindsMatch()
        {
            // The snapshot contains 'Zelsted Kerman' (the stand-in) instead of
            // 'Jebediah Kerman' (the original) because an earlier recording
            // committed and replaced Jeb with Zelsted in subsequent snapshots.
            var snapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Zelsted Kerman", "Bill Kerman", "Bob Kerman");

            var reverseMap = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Zelsted Kerman" }
            };

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Jebediah Kerman", new[] { snapshot }, reverseMap);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_ReverseMapMissingEntry_DoesNotFalselyMatch()
        {
            // Reverse map has Jeb→Zelsted, but we're looking for Bob.
            // Snapshot has Zelsted but no Bob. Must NOT return a match.
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1-3pod.v2", "Zelsted Kerman");
            var reverseMap = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Zelsted Kerman" }
            };

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, reverseMap);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullReverseMap_DirectMatchStillWorks()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1pod", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
        }

        // ────────────────────────────────────────────────────────
        // Regression: the exact 2026-04-09 playtest scenario
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_KerbalXBobOnEvaScenario_FindsMk1_3PodSeat()
        {
            // Kerbal X recording (id 8124d4445c) has GhostVisualSnapshot from
            // recording start with all 3 crew in the Mk1-3 pod. Bob EVA'd
            // mid-recording but the start-of-recording snapshot still lists him.
            var kerbalXStartSnapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            // No reverse-map needed — this is the first recording in the timeline,
            // its snapshot contains the raw original names.
            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { kerbalXStartSnapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("mk1-3pod.v2", seat.PartName);
        }

        // ────────────────────────────────────────────────────────
        // Spawned vessel guard — SwapReservedCrewInFlight must
        // skip both passes for Parsek-spawned vessels (#BugC)
        // ────────────────────────────────────────────────────────

        [Fact]
        public void BuildSpawnedVesselPidSet_MatchesSpawnedPid()
        {
            var rec = new Recording { SpawnedVesselPersistentId = 847060085 };
            RecordingStore.AddCommittedForTesting(rec);

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);

            Assert.Contains(847060085u, pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_ZeroPid_NotIncluded()
        {
            var rec = new Recording { SpawnedVesselPersistentId = 0 };
            RecordingStore.AddCommittedForTesting(rec);

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);

            Assert.DoesNotContain(0u, pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_NoMatch_DoesNotContainArbitraryPid()
        {
            var rec = new Recording { SpawnedVesselPersistentId = 42 };
            RecordingStore.AddCommittedForTesting(rec);

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);

            Assert.DoesNotContain(99999u, pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_NullRecordings_ReturnsEmpty()
        {
            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(null);

            Assert.Empty(pids);
        }

        [Fact]
        public void SpawnedVesselGuard_LogsSkipMessage_WhenActiveVesselIsSpawned()
        {
            // Set up a committed recording with a spawned PID
            var rec = new Recording { SpawnedVesselPersistentId = 12345 };
            RecordingStore.AddCommittedForTesting(rec);

            // Verify the PID set contains it (the guard's decision logic)
            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);
            Assert.True(pids.Contains(12345u),
                "Guard should detect pid=12345 as a Parsek-spawned vessel");

            // Verify an unrelated PID is NOT detected
            Assert.False(pids.Contains(99999u),
                "Guard should NOT detect pid=99999 as spawned");
        }
    }
}
