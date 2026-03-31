using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #170 fixes: KSC exclusion zone and dead crew spawn guard.
    /// </summary>
    [Collection("Sequential")]
    public class Bug170Tests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug170Tests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  IsWithinKscExclusionZone
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void KscExclusion_AtPad_ReturnsTrue()
        {
            // Exactly at the KSC launch pad coordinates
            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0, // Kerbin radius
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);

            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("[SpawnCollision]") && l.Contains("IsWithinKscExclusionZone"));
        }

        [Fact]
        public void KscExclusion_4mFromPad_ReturnsTrue()
        {
            // 4m from pad — the actual bug distance. ~0.0000004 degrees at Kerbin equator.
            double offsetDeg = 4.0 / (600000.0 * Math.PI / 180.0);
            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude + offsetDeg,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);

            Assert.True(result);
        }

        [Fact]
        public void KscExclusion_100mFromPad_ReturnsTrue()
        {
            // 100m from pad — still within 150m exclusion zone
            double offsetDeg = 100.0 / (600000.0 * Math.PI / 180.0);
            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude,
                SpawnCollisionDetector.KscPadLongitude + offsetDeg,
                600000.0,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);

            Assert.True(result);
        }

        [Fact]
        public void KscExclusion_200mFromPad_ReturnsFalse()
        {
            // 200m from pad — outside the 150m exclusion zone
            double offsetDeg = 200.0 / (600000.0 * Math.PI / 180.0);
            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude + offsetDeg,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);

            Assert.False(result);
        }

        [Fact]
        public void KscExclusion_FarAway_ReturnsFalse()
        {
            // Island airfield — ~25km from KSC, well outside any exclusion zone
            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                -1.5174, -71.9653, // Island airfield approximate coords
                600000.0,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);

            Assert.False(result);
        }

        [Fact]
        public void KscExclusion_ExactBoundary_OutsideAtRadiusPlus1()
        {
            // Place at exactly exclusionRadius + 1m north of pad
            double exclusion = SpawnCollisionDetector.DefaultKscExclusionRadiusMeters;
            double offsetDeg = (exclusion + 1.0) / (600000.0 * Math.PI / 180.0);

            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude + offsetDeg,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0,
                exclusion);

            Assert.False(result);
        }

        [Fact]
        public void KscExclusion_CustomRadius_Respected()
        {
            // 50m from pad with 30m exclusion radius — should be outside
            double offsetDeg = 50.0 / (600000.0 * Math.PI / 180.0);
            bool result = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude + offsetDeg,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0,
                30.0);

            Assert.False(result);

            // Same distance with 60m radius — should be inside
            bool result2 = SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude + offsetDeg,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0,
                60.0);

            Assert.True(result2);
        }

        [Fact]
        public void KscExclusion_LogsDistance()
        {
            SpawnCollisionDetector.IsWithinKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude,
                SpawnCollisionDetector.KscPadLongitude,
                600000.0,
                150.0);

            Assert.Contains(logLines, l => l.Contains("distance=") && l.Contains("radius="));
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldBlockSpawnForDeadCrew
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void DeadCrewGuard_NoCrew_DoesNotBlock()
        {
            var crew = new List<string>();
            var dead = new HashSet<string> { "Jeb" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, dead);

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("no crew in snapshot"));
        }

        [Fact]
        public void DeadCrewGuard_NullCrew_DoesNotBlock()
        {
            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(null, new HashSet<string> { "Jeb" });

            Assert.False(result);
        }

        [Fact]
        public void DeadCrewGuard_NoDeadCrew_DoesNotBlock()
        {
            var crew = new List<string> { "Jeb", "Bill" };
            var dead = new HashSet<string>();

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, dead);

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("no dead crew"));
        }

        [Fact]
        public void DeadCrewGuard_AllDead_Blocks()
        {
            var crew = new List<string> { "Minidou Kerman" };
            var dead = new HashSet<string> { "Minidou Kerman" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, dead);

            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("blocking spawn (all crew dead)"));
        }

        [Fact]
        public void DeadCrewGuard_AllMultipleDead_Blocks()
        {
            var crew = new List<string> { "Jeb", "Bill", "Bob" };
            var dead = new HashSet<string> { "Jeb", "Bill", "Bob" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, dead);

            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("3/3 crew dead"));
        }

        [Fact]
        public void DeadCrewGuard_SomeAlive_DoesNotBlock()
        {
            var crew = new List<string> { "Jeb", "Bill" };
            var dead = new HashSet<string> { "Jeb" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, dead);

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("1/2 crew dead") && l.Contains("allowing spawn"));
        }

        [Fact]
        public void DeadCrewGuard_NullDeadSet_DoesNotBlock()
        {
            var crew = new List<string> { "Jeb" };

            bool result = VesselSpawner.ShouldBlockSpawnForDeadCrew(crew, null);

            Assert.False(result);
        }

        // ────────────────────────────────────────────────────────────
        //  ExtractCrewNamesFromSnapshot
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ExtractCrew_NullSnapshot_EmptyList()
        {
            var result = VesselSpawner.ExtractCrewNamesFromSnapshot(null);

            Assert.Empty(result);
        }

        [Fact]
        public void ExtractCrew_NoCrew_EmptyList()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("name", "mk1pod.v2");
            snapshot.AddNode(part);

            var result = VesselSpawner.ExtractCrewNamesFromSnapshot(snapshot);

            Assert.Empty(result);
        }

        [Fact]
        public void ExtractCrew_SingleCrew_ReturnsCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("crew", "Minidou Kerman");
            snapshot.AddNode(part);

            var result = VesselSpawner.ExtractCrewNamesFromSnapshot(snapshot);

            Assert.Single(result);
            Assert.Equal("Minidou Kerman", result[0]);
        }

        [Fact]
        public void ExtractCrew_MultipleParts_AggregatesCrew()
        {
            var snapshot = new ConfigNode("VESSEL");

            var part1 = new ConfigNode("PART");
            part1.AddValue("crew", "Jeb");
            part1.AddValue("crew", "Bill");
            snapshot.AddNode(part1);

            var part2 = new ConfigNode("PART");
            part2.AddValue("crew", "Bob");
            snapshot.AddNode(part2);

            var result = VesselSpawner.ExtractCrewNamesFromSnapshot(snapshot);

            Assert.Equal(3, result.Count);
            Assert.Contains("Jeb", result);
            Assert.Contains("Bill", result);
            Assert.Contains("Bob", result);
        }

        [Fact]
        public void ExtractCrew_SkipsEmptyNames()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jeb");
            part.AddValue("crew", "");
            snapshot.AddNode(part);

            var result = VesselSpawner.ExtractCrewNamesFromSnapshot(snapshot);

            Assert.Single(result);
            Assert.Equal("Jeb", result[0]);
        }
    }
}
