using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// ROCK-SPAWN-BOUNDS-TOO-SMALL: the spawn space check sizes an asteroid or comet part by
    /// its own size class, not the 1.25 m half extent of an ordinary part.
    /// </summary>
    [Collection("Sequential")]
    public class RockSpawnBoundsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RockSpawnBoundsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode ClawedShip(string prefabUrl)
        {
            var b = VesselSnapshotBuilder.ClawedAsteroidShip("Grabber", "Jebediah Kerman", 500u);
            if (prefabUrl != null)
                b.AddModuleToPart(2, "ModuleAsteroid", ("seed", "12345"), ("prefabBaseURL", prefabUrl));
            return b.Build();
        }

        // ---- size class parsing ----

        [Theory]
        [InlineData("Procedural/PA_A", 0)]
        [InlineData("Procedural/PA_E", 4)]
        [InlineData("Procedural/PC_I", 8)]
        [InlineData(" Procedural/PA_c ", 2)]
        public void TryParseSizeClassFromPrefabUrl_ReadsTheClassLetter(string url, int expected)
        {
            Assert.True(VesselSnapshotOps.TryParseSizeClassFromPrefabUrl(url, out int cls));
            Assert.Equal(expected, cls);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Procedural/PA_")]
        [InlineData("Procedural/PA_J")]
        [InlineData("Procedural/PA_EE")]
        [InlineData("Comets/CustomComet")]
        public void TryParseSizeClassFromPrefabUrl_RejectsUnreadable(string url)
        {
            Assert.False(VesselSnapshotOps.TryParseSizeClassFromPrefabUrl(url, out int cls));
            Assert.Equal(-1, cls);
        }

        [Fact]
        public void NominalRadius_IsTheDecompiledStockTable_OutOfRangeIsLargest()
        {
            float[] expected = { 3.24f, 5.4f, 9f, 15f, 25f, 41.7f, 69.5f, 115.8f, 193f };
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], VesselSnapshotOps.NominalRadiusForClass(i));
            Assert.Equal(193f, VesselSnapshotOps.NominalRadiusForClass(-1));
            Assert.Equal(193f, VesselSnapshotOps.NominalRadiusForClass(9));
            Assert.Equal("E", VesselSnapshotOps.SizeClassLetter(4));
            Assert.Equal("?", VesselSnapshotOps.SizeClassLetter(12));
        }

        [Fact]
        public void TryReadDiscoverySizeClass_ReadsTheSizeInt()
        {
            ConfigNode snap = new VesselSnapshotBuilder().WithType("SpaceObject").AddPart("PotatoRoid").Build();
            Assert.True(VesselSnapshotOps.TryReadDiscoverySizeClass(snap, out int cls));
            Assert.Equal(2, cls); // the builder writes DISCOVERY size = 2 (class C)
            snap.GetNode("DISCOVERY").SetValue("size", "42");
            Assert.False(VesselSnapshotOps.TryReadDiscoverySizeClass(snap, out _));
            Assert.False(VesselSnapshotOps.TryReadDiscoverySizeClass(null, out _));
        }

        // ---- per-part half extent ----

        [Fact]
        public void OrdinaryPart_KeepsTheDefaultHalfExtent()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "mk1pod.v2");
            float half = SpawnCollisionDetector.ResolvePartHalfExtent(part, null, out string source);
            Assert.Equal(1.25f, half);
            Assert.Equal(SpawnCollisionDetector.PartExtentSourceDefault, source);
        }

        [Fact]
        public void ClassEAsteroid_FromItsPrefabUrl_IsAboutFortyMetres()
        {
            ConfigNode ship = ClawedShip("Procedural/PA_E");
            ConfigNode rock = ship.GetNodes("PART")[2];
            float half = SpawnCollisionDetector.ResolvePartHalfExtent(rock, ship, out string source);
            Assert.Equal(25f * 1.25f * 1.25f, half, 0.001f);
            Assert.Equal("prefab:E", source);
        }

        [Fact]
        public void CometModule_IsARockPartToo()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "SomeModdedRock");
            ConfigNode m = part.AddNode("MODULE");
            m.AddValue("name", "ModuleComet");
            m.AddValue("prefabBaseURL", "Procedural/PC_D");
            float half = SpawnCollisionDetector.ResolvePartHalfExtent(part, null, out string source);
            Assert.Equal(15f * 1.25f * 1.25f, half, 0.001f);
            Assert.Equal("prefab:D", source);
        }

        [Fact]
        public void BareRock_WithNoModule_FallsBackToItsVesselDiscoverySize()
        {
            ConfigNode rockVessel = new VesselSnapshotBuilder()
                .WithType("SpaceObject").AddPart("PotatoRoid").Build();
            ConfigNode rock = rockVessel.GetNodes("PART")[0];
            float half = SpawnCollisionDetector.ResolvePartHalfExtent(rock, rockVessel, out string source);
            Assert.Equal(9f * 1.25f * 1.25f, half, 0.001f);
            Assert.Equal("discovery:C", source);
        }

        [Fact]
        public void CarriedRock_WithNoReadableClass_AssumesTheLargestClass()
        {
            // The ship's DISCOVERY is the ship's, not the rock's, so it is not read.
            ConfigNode ship = ClawedShip(null);
            ConfigNode rock = ship.GetNodes("PART")[2];
            float half = SpawnCollisionDetector.ResolvePartHalfExtent(rock, ship, out string source);
            Assert.Equal(193f * 1.25f * 1.25f, half, 0.001f);
            Assert.Equal(SpawnCollisionDetector.PartExtentSourceFallbackLargest, source);
        }

        // ---- the vessel bounds ----

        [Fact]
        public void ShipCarryingAClassERock_BoundsCoverTheRock()
        {
            ConfigNode ship = ClawedShip("Procedural/PA_E");
            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(ship);

            float rockHalf = 25f * 1.25f * 1.25f;
            // The rock sits at y = -3.2; its cube dominates every axis.
            Assert.Equal(rockHalf * 2f, bounds.size.x, 0.01f);
            Assert.True(bounds.size.y >= rockHalf * 2f);
            Assert.Equal(-3.2f - rockHalf, bounds.min.y, 0.01f);
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]")
                && l.Contains("rock part 'PotatoRoid' half extent 39.1m source=prefab:E"));
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]")
                && l.Contains("ParsePartPositions: parsed 3/3 parts from snapshot (1 asteroid / comet)"));
        }

        [Fact]
        public void ShipCarryingARock_NowOverlapsAVesselThatTheOldBoundsCleared()
        {
            // A loaded 2 m vessel 20 m below the claw: clear of a few-metre ship, inside a
            // class E rock.
            ConfigNode ship = ClawedShip("Procedural/PA_E");
            Bounds shipBounds = SpawnCollisionDetector.ComputeVesselBounds(ship);
            var other = new Bounds(Vector3.zero, Vector3.one * 2f);
            var oldShip = SpawnCollisionDetector.ComputeBoundsFromParts(
                new List<(Vector3 localPos, float halfExtent)>
                {
                    (new Vector3(0f, 0f, 0f), 1.25f),
                    (new Vector3(0f, -1f, 0f), 1.25f),
                    (new Vector3(0f, -3.2f, 0f), 1.25f),
                });

            Assert.False(SpawnCollisionDetector.BoundsOverlap(
                oldShip, Vector3d.zero, other, new Vector3d(0, -20, 0), 0.5f));
            Assert.True(SpawnCollisionDetector.BoundsOverlap(
                shipBounds, Vector3d.zero, other, new Vector3d(0, -20, 0), 0.5f));
        }

        [Fact]
        public void UnreadableRockClass_LogsTheLargestClassFallbackAtInfo()
        {
            SpawnCollisionDetector.ComputeVesselBounds(ClawedShip(null));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][SpawnCollision]")
                && l.Contains("source=fallback-largest")
                && l.Contains("largest stock class assumed"));
        }

        [Fact]
        public void ShipWithNoRock_BoundsUnchanged()
        {
            ConfigNode ship = VesselSnapshotBuilder.CrewedShip("Plain", "Jebediah Kerman", 600u).Build();
            Bounds bounds = SpawnCollisionDetector.ComputeVesselBounds(ship);
            Assert.Equal(2.5f, bounds.size.x, 0.001f);
            Assert.DoesNotContain(logLines, l => l.Contains("rock part"));
        }
    }
}
