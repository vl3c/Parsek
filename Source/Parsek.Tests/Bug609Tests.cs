using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #609 (spawner-side downstream of #608): reserved kerbals
    /// that KSP marked Missing (because their original vessel was stripped by
    /// Re-Fly) must not be counted as "dead" by the all-crew-dead spawn-block
    /// guard — they will be rescued to Available before the snapshot is
    /// loaded, so the spawn should proceed.
    ///
    /// Live-roster behaviour (the actual rescue, the BuildDeadCrewSet
    /// reservation carve-out, and the un-block Verbose log) is exercised by
    /// the in-game test
    /// <c>RuntimeTests.Bug609_ReservedMissingCrewIsSpawnableAndRescued</c>.
    /// </summary>
    [Collection("Sequential")]
    public class Bug609Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug609Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  ClassifySnapshotCrew (degraded path: no live roster → all Alive)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ClassifySnapshotCrew_NullSnapshotCrew_ReturnsEmpty()
        {
            var result = VesselSpawner.ClassifySnapshotCrew(null);
            Assert.Empty(result);
        }

        [Fact]
        public void ClassifySnapshotCrew_EmptyList_ReturnsEmpty()
        {
            var result = VesselSpawner.ClassifySnapshotCrew(new List<string>());
            Assert.Empty(result);
        }

        [Fact]
        public void ClassifySnapshotCrew_NoRoster_AllClassifiedAsAlive()
        {
            // xUnit has no HighLogic.CurrentGame.CrewRoster, so every name
            // falls through to Alive. This is the degraded-path contract:
            // when the roster cannot be inspected, the spawn-block guard
            // should NOT incorrectly classify anyone as dead.
            var crew = new List<string> { "Jeb", "Bill", "Bob" };
            var result = VesselSpawner.ClassifySnapshotCrew(crew);

            Assert.Equal(3, result.Count);
            for (int i = 0; i < result.Count; i++)
                Assert.Equal(VesselSpawner.SpawnableClassification.Alive, result[i].Value);
            Assert.Equal("Jeb", result[0].Key);
            Assert.Equal("Bill", result[1].Key);
            Assert.Equal("Bob", result[2].Key);
        }

        // ────────────────────────────────────────────────────────────
        //  FormatSpawnableClassificationSummary (pure)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void FormatSummary_AllCategories_RendersCountsAndNames()
        {
            var classified = new List<KeyValuePair<string, VesselSpawner.SpawnableClassification>>
            {
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Jeb", VesselSpawner.SpawnableClassification.ReservedMissingRescuable),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Bill", VesselSpawner.SpawnableClassification.MissingNotReserved),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Bob", VesselSpawner.SpawnableClassification.StrictlyDead),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Val", VesselSpawner.SpawnableClassification.Alive),
            };

            string s = VesselSpawner.FormatSpawnableClassificationSummary(classified);

            Assert.Contains("total=4", s);
            Assert.Contains("strictlyDead=1", s);
            Assert.Contains("missingNotReserved=1", s);
            Assert.Contains("reservedMissing=1", s);
            Assert.Contains("alive=1", s);
            Assert.Contains("Jeb: ReservedMissingRescuable", s);
            Assert.Contains("Bill: MissingNotReserved", s);
            Assert.Contains("Bob: StrictlyDead", s);
            Assert.Contains("Val: Alive", s);
        }

        [Fact]
        public void FormatSummary_AllStrictlyDead_RendersCorrectCounts()
        {
            // The shape that #608 abandon log will produce when the spawn
            // really should be abandoned (all crew permanently Dead).
            var classified = new List<KeyValuePair<string, VesselSpawner.SpawnableClassification>>
            {
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Jeb", VesselSpawner.SpawnableClassification.StrictlyDead),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Bill", VesselSpawner.SpawnableClassification.StrictlyDead),
                new KeyValuePair<string, VesselSpawner.SpawnableClassification>(
                    "Bob", VesselSpawner.SpawnableClassification.StrictlyDead),
            };

            string s = VesselSpawner.FormatSpawnableClassificationSummary(classified);

            Assert.Contains("total=3", s);
            Assert.Contains("strictlyDead=3", s);
            Assert.Contains("missingNotReserved=0", s);
            Assert.Contains("reservedMissing=0", s);
            Assert.Contains("alive=0", s);
        }

        [Fact]
        public void FormatSummary_EmptyList_RendersZeroCounts()
        {
            string s = VesselSpawner.FormatSpawnableClassificationSummary(
                new List<KeyValuePair<string, VesselSpawner.SpawnableClassification>>());

            Assert.Contains("total=0", s);
            Assert.Contains("strictlyDead=0", s);
            Assert.Contains("missingNotReserved=0", s);
            Assert.Contains("reservedMissing=0", s);
            Assert.Contains("alive=0", s);
            Assert.Contains("[]", s);
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldBlockSpawnForDeadCrew — same pure surface, asserting
        //  the post-#608 contract: a deadSet that excludes reserved+Missing
        //  crew (because BuildDeadCrewSet now does the carve-out) must NOT
        //  block when only reserved+Missing names were in the snapshot.
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldBlock_DeadSetExcludesReservedMissing_DoesNotBlock()
        {
            // Simulates the Re-Fly scenario AFTER BuildDeadCrewSet has
            // applied the #608 carve-out: snapshot has [Jeb, Bill, Bob],
            // they were Missing in the roster but reserved, so the dead
            // set is empty. ShouldBlockSpawnForDeadCrew must allow.
            var crew = new List<string> { "Jeb", "Bill", "Bob" };
            var deadSet = new HashSet<string>(); // carve-out emptied it

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, deadSet);

            Assert.False(result);
        }

        [Fact]
        public void ShouldBlock_OnlyOneOfThreeReservedMissing_DoesNotBlock()
        {
            // Mixed: Jeb is reserved+Missing (carved out), Bill is alive,
            // Bob is strictly Dead. Two are spawnable, one is dead → allow.
            var crew = new List<string> { "Jeb", "Bill", "Bob" };
            var deadSet = new HashSet<string> { "Bob" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, deadSet);

            Assert.False(result);
        }
    }
}
