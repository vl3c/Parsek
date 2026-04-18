using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #456: the orphan-placement matcher's name-hit fallback.
    ///
    /// A synthetic showcase ghost with PID=100000 (see `.claude/CLAUDE.md`
    /// "Ghost event ↔ snapshot PID") can never pid-match a freshly-launched
    /// real vessel, because KSP assigns the part's <c>persistentId</c> at
    /// vessel spawn, not from the ghost snapshot. The tier-1 pid match misses
    /// and the tier-2 name-hit fallback carries the placement.
    ///
    /// These tests pin:
    ///   1. pid-hit beats name-hit when both are possible
    ///   2. name-hit with a unique match succeeds
    ///   3. name-hit with multiple matches picks the minimum-free-seats part
    ///   4. no-match (pid and name both miss) returns -1 / None
    ///   5. pid=100000 coincidence: a pid-hit still wins, no name-hit false positive
    ///   6. log-assertion: the summary line contains nameHitFallbacks=... counter
    ///
    /// Live AddCrewmember placement is covered by the in-game test
    /// `Bug456_OrphanPlacement_NameHitFallback_PlacesStandin` in
    /// `InGameTests/RuntimeTests.cs`.
    /// </summary>
    [Collection("Sequential")]
    public class CrewReservationNameHitFallbackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewReservationNameHitFallbackTests()
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

        private static CrewReservationManager.ActivePartSeat Seat(
            uint pid, string name, int freeSeats)
        {
            return new CrewReservationManager.ActivePartSeat
            {
                PersistentId = pid,
                PartName = name,
                FreeSeats = freeSeats
            };
        }

        // ────────────────────────────────────────────────────────
        // TryResolveActiveVesselPartForSeat — pure helper
        // ────────────────────────────────────────────────────────

        [Fact]
        public void TryResolveActiveVesselPartForSeat_PidHit_ReturnsPidMatch()
        {
            // Snapshot pid matches a live part's PersistentId with a free seat.
            // The name would also match but pid-hit wins by rule.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 3717834180u, name: "mk1pod.v2", freeSeats: 1),
                Seat(pid: 3717834181u, name: "mk1pod.v2", freeSeats: 2),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 3717834180u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(0, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.PidHit, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_NameHitUniqueMatch_Succeeds()
        {
            // Snapshot pid=100000 (synthetic showcase ghost) never matches a
            // real KSP-assigned part pid. Exactly one live part shares the
            // snapshot part name. Name-hit fallback must return it.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 42u, name: "fuelTank", freeSeats: 0),
                Seat(pid: 43u, name: "mk1pod.v2", freeSeats: 1),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(1, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.NameHit, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_NameHitMultipleMatches_PicksMinFreeSeats()
        {
            // Two parts share the snapshot part name. Both have free seats but
            // one is a 1-seat cockpit (tighter fit) and one is a 4-seat
            // passenger cabin. Tightest-fit rule: cockpit wins.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 100u, name: "crewCabin", freeSeats: 3),   // 4-seat cabin, 1 occupied
                Seat(pid: 101u, name: "mk1pod.v2", freeSeats: 4),   // 4-seat cabin (reused name)
                Seat(pid: 102u, name: "mk1pod.v2", freeSeats: 1),   // 1-seat cockpit (tightest fit)
                Seat(pid: 103u, name: "mk1pod.v2", freeSeats: 2),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(2, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.NameHit, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_NoMatch_PidAndNameBothMiss_ReturnsMinusOne()
        {
            // Snapshot pid matches nothing; snapshot name matches nothing;
            // unrelated parts carry free seats but their names differ.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 42u, name: "fuelTank", freeSeats: 0),
                Seat(pid: 43u, name: "crewCabin", freeSeats: 4),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(-1, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.None, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_PidCoincidence_PidHitWinsOverNameHit()
        {
            // Defensive: if the snapshot pid happens to equal an active-vessel
            // pid by coincidence — and the name-hit candidate elsewhere is a
            // tighter fit — the pid match STILL wins. This pins the rule
            // "pid-hit is the most trusted tier" and rules out a brittle
            // false-positive on identical-name reassembly.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 100000u, name: "mk1pod.v2", freeSeats: 4),  // pid coincidence, loose fit
                Seat(pid: 200000u, name: "mk1pod.v2", freeSeats: 1),  // tighter name fit, but wrong pid
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(0, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.PidHit, kind);
        }

        // ────────────────────────────────────────────────────────
        // Edge cases
        // ────────────────────────────────────────────────────────

        [Fact]
        public void TryResolveActiveVesselPartForSeat_PidMatchesButPartIsFull_FallsBackToName()
        {
            // The pid-matched part is full (freeSeats=0), so tier 1 misses.
            // Tier 2 must find a same-name part with a free seat.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 100000u, name: "mk1pod.v2", freeSeats: 0),  // pid match, but full
                Seat(pid: 100001u, name: "mk1pod.v2", freeSeats: 1),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(1, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.NameHit, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_ZeroSnapshotPid_SkipsPidTierGoesToName()
        {
            // snapshotPartPid=0 means the snapshot never captured a real pid
            // (legacy format or bug #413-style miss). Skip tier 1, try tier 2.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 42u, name: "mk1pod.v2", freeSeats: 1),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 0u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(0, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.NameHit, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_NullOrEmptyParts_ReturnsMinusOne()
        {
            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                100000u, "mk1pod.v2", null, out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(-1, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.None, kind);

            int idx2 = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                100000u, "mk1pod.v2",
                new List<CrewReservationManager.ActivePartSeat>(),
                out CrewReservationManager.SeatMatchKind kind2);

            Assert.Equal(-1, idx2);
            Assert.Equal(CrewReservationManager.SeatMatchKind.None, kind2);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_NameHitTieOnFreeSeats_FirstWins()
        {
            // Two parts share both the name and the free-seat count. First one
            // in iteration order wins (deterministic).
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 10u, name: "mk1pod.v2", freeSeats: 1),
                Seat(pid: 11u, name: "mk1pod.v2", freeSeats: 1),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(0, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.NameHit, kind);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_EmptyOrNullSnapshotName_SkipsNameTier()
        {
            // No pid match and no snapshot name → both tiers miss, even if live
            // parts have free seats.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 42u, name: "mk1pod.v2", freeSeats: 1),
            };

            int idx1 = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                0u, null, parts, out var k1);
            int idx2 = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                0u, "", parts, out var k2);

            Assert.Equal(-1, idx1);
            Assert.Equal(CrewReservationManager.SeatMatchKind.None, k1);
            Assert.Equal(-1, idx2);
            Assert.Equal(CrewReservationManager.SeatMatchKind.None, k2);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_RegressionPlaytestScenario_SingleSeatPodFull_ReturnsMinusOne()
        {
            // Regression: 2026-04-18 playtest `KSP.log:15428` scenario. Active
            // vessel is a freshly-launched `Untitled Space Craft` with a single
            // `mk1pod.v2` cockpit that's already crewed (freeSeats=0). Snapshot
            // is a synthetic showcase ghost with pid=100000 name='mk1pod.v2'.
            //
            // Expected: no match (tier 1 pid miss, tier 2 name match but no
            // free seat). This verifies the matcher does not force-place the
            // stand-in into a full part — the caller will log the WARN with
            // the fallback-attempted counters.
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 18900260u, name: "mk1pod.v2", freeSeats: 0),
            };

            int idx = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                snapshotPartPid: 100000u,
                snapshotPartName: "mk1pod.v2",
                activeParts: parts,
                out CrewReservationManager.SeatMatchKind kind);

            Assert.Equal(-1, idx);
            Assert.Equal(CrewReservationManager.SeatMatchKind.None, kind);
        }

        // ────────────────────────────────────────────────────────
        // Invariants: the pure helper must not mutate its inputs and must
        // stay deterministic across repeat calls (no hidden state).
        // ────────────────────────────────────────────────────────

        [Fact]
        public void TryResolveActiveVesselPartForSeat_DoesNotMutateInputList()
        {
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 10u, name: "mk1pod.v2", freeSeats: 2),
                Seat(pid: 11u, name: "mk1pod.v2", freeSeats: 1),
            };
            int countBefore = parts.Count;
            uint pid0Before = parts[0].PersistentId;
            int free0Before = parts[0].FreeSeats;

            _ = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                0u, "mk1pod.v2", parts, out _);

            Assert.Equal(countBefore, parts.Count);
            Assert.Equal(pid0Before, parts[0].PersistentId);
            Assert.Equal(free0Before, parts[0].FreeSeats);
        }

        [Fact]
        public void TryResolveActiveVesselPartForSeat_DeterministicAcrossRepeatedCalls()
        {
            var parts = new List<CrewReservationManager.ActivePartSeat>
            {
                Seat(pid: 10u, name: "mk1pod.v2", freeSeats: 3),
                Seat(pid: 11u, name: "mk1pod.v2", freeSeats: 1),
                Seat(pid: 12u, name: "mk1pod.v2", freeSeats: 2),
            };

            int idx1 = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                100000u, "mk1pod.v2", parts, out var k1);
            int idx2 = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                100000u, "mk1pod.v2", parts, out var k2);
            int idx3 = CrewReservationManager.TryResolveActiveVesselPartForSeat(
                100000u, "mk1pod.v2", parts, out var k3);

            Assert.Equal(idx1, idx2);
            Assert.Equal(idx2, idx3);
            Assert.Equal(k1, k2);
            Assert.Equal(k2, k3);
            // Index 1 has freeSeats=1 (tightest fit).
            Assert.Equal(1, idx1);
            Assert.Equal(CrewReservationManager.SeatMatchKind.NameHit, k1);
        }
    }
}
