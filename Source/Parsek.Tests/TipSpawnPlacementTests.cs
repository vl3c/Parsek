using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Where an end-of-recording ("tip") vessel is placed: the collision walkback reads only
    /// points that place on the spawn body (never a Relative-section anchor offset as
    /// lat/lon), a Splashed spawn is floored to sea level on every path, a sea spawn is
    /// seen as over water, and a chain tip follows the end-of-recording rules for dead
    /// crew and for where an orbital tip is collision-checked.
    /// </summary>
    [Collection("Sequential")]
    public class TipSpawnPlacementTests : IDisposable
    {
        private const double R = 600000.0;
        private readonly List<string> logLines = new List<string>();

        public TipSpawnPlacementTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            SpawnWarpOutcomeWatch.ClearAbandonNote("tip-dead-crew");
            SpawnWarpOutcomeWatch.ClearAbandonNote("tip-eva");
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Vector3d ToWorld(double lat, double lon, double alt)
        {
            double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0, r = R + alt;
            return new Vector3d(r * Math.Cos(la) * Math.Cos(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Sin(lo));
        }

        private static TrajectoryPoint Pt(double ut, double lat, double lon, double alt, string body = "Kerbin")
        {
            return new TrajectoryPoint { ut = ut, bodyName = body, latitude = lat, longitude = lon, altitude = alt };
        }

        // A lander that approached a base inside a RELATIVE section (anchor-local metre
        // offsets in the lat/lon/alt fields) and landed: the recorder leaves Relative on
        // landing, so the last points are body-fixed surface points.
        private static (List<TrajectoryPoint> points, List<TrackSection> sections) LanderNearBase(
            bool withShadow)
        {
            var rel0 = Pt(100, 12.0, -35.0, 8.0);
            var rel1 = Pt(101, 11.5, -34.0, 4.0);
            var surface = new List<TrajectoryPoint>
            {
                Pt(102, -0.09720, -74.55700, 70.0),
                Pt(103, -0.09722, -74.55705, 70.0),
                Pt(104, -0.09724, -74.55710, 70.0),
            };
            var relative = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100,
                endUT = 101,
                frames = new List<TrajectoryPoint> { rel0, rel1 },
                bodyFixedFrames = withShadow
                    ? new List<TrajectoryPoint>
                    {
                        // ~110 m and ~55 m north of the landing site
                        Pt(100, -0.08724, -74.55700, 90.0),
                        Pt(101, -0.09224, -74.55700, 80.0),
                    }
                    : null,
            };
            var absolute = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 102,
                endUT = 104,
                frames = new List<TrajectoryPoint>(surface),
            };
            var points = new List<TrajectoryPoint> { rel0, rel1 };
            points.AddRange(surface);
            return (points, new List<TrackSection> { relative, absolute });
        }

        // ---- G: the walkback never reads a Relative anchor offset as lat/lon ----

        [Fact]
        public void Walkback_RelativeOffsetsWithoutShadow_AreNotWalkedInto()
        {
            var (points, sections) = LanderNearBase(withShadow: false);
            Vector3d end = ToWorld(points[4].latitude, points[4].longitude, points[4].altitude);
            Func<Vector3d, bool> overlaps = p => (p - end).magnitude < 30.0;

            List<TrajectoryPoint> walkable = SpawnCollisionDetector.ResolveWalkbackPointsOnBody(
                points, sections, "Kerbin", "test");
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                walkable, R, SpawnCollisionDetector.DefaultWalkbackStepMeters, ToWorld, overlaps);

            // Before the fix the walk interpolated toward (12, -35) read as degrees and
            // returned a candidate 885 m off the path. Now the walk ends with the surface run.
            Assert.Equal(3, walkable.Count);
            Assert.Equal(102, walkable[0].ut);
            Assert.False(result.found,
                "walkback found a candidate off the surface run at lat=" + result.lat + " lon=" + result.lon);
            Assert.Contains(logLines, l => l.Contains("[SpawnCollision]")
                && l.Contains("Walkback points resolved")
                && l.Contains("stop=relative-no-shadow")
                && l.Contains("kept=3/5"));
        }

        [Fact]
        public void Walkback_RelativeOffsetsWithShadow_WalkTheBodyFixedShadow()
        {
            var (points, sections) = LanderNearBase(withShadow: true);
            Vector3d end = ToWorld(points[4].latitude, points[4].longitude, points[4].altitude);
            Func<Vector3d, bool> overlaps = p => (p - end).magnitude < 30.0;

            List<TrajectoryPoint> walkable = SpawnCollisionDetector.ResolveWalkbackPointsOnBody(
                points, sections, "Kerbin", "test");
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                walkable, R, SpawnCollisionDetector.DefaultWalkbackStepMeters, ToWorld, overlaps);

            Assert.Equal(5, walkable.Count);
            Assert.Equal(-0.08724, walkable[0].latitude);
            Assert.Equal(-0.09224, walkable[1].latitude);
            Assert.True(result.found);
            double dist = (result.worldPos - end).magnitude;
            Assert.True(dist >= 30.0 && dist < 120.0,
                "walkback candidate is " + dist.ToString("F0") + " m from the landing site");
            Assert.Contains(logLines, l => l.Contains("relativeShadowsUsed=2") && l.Contains("stop=none"));
        }

        [Fact]
        public void Walkback_StopsAtAnSoiCrossing()
        {
            var points = new List<TrajectoryPoint>
            {
                Pt(10, 3.0, 40.0, 5000.0, "Mun"),
                Pt(11, 3.1, 40.1, 4000.0, "Mun"),
                Pt(12, -0.0972, -74.5570, 70.0),
                Pt(13, -0.0973, -74.5571, 70.0),
            };

            List<TrajectoryPoint> walkable = SpawnCollisionDetector.ResolveWalkbackPointsOnBody(
                points, null, "Kerbin", "test");

            Assert.Equal(2, walkable.Count);
            Assert.All(walkable, p => Assert.Equal("Kerbin", p.bodyName));
            Assert.Contains(logLines, l => l.Contains("stop=body-change(Mun)") && l.Contains("stopIndex=1"));
        }

        [Fact]
        public void Walkback_RelativeEndpoint_LeavesNothingToWalk()
        {
            var rel = Pt(5, 2.0, -3.0, 1.0);
            var points = new List<TrajectoryPoint> { Pt(4, -0.0972, -74.5570, 70.0), rel };
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 5,
                    endUT = 5,
                    frames = new List<TrajectoryPoint> { rel },
                },
            };

            Assert.Empty(SpawnCollisionDetector.ResolveWalkbackPointsOnBody(points, sections, "Kerbin", "test"));
        }

        [Fact]
        public void Walkback_PlainAbsoluteTrajectory_IsKeptWholeAndSilent()
        {
            var points = new List<TrajectoryPoint>
            {
                Pt(1, -0.0970, -74.5570, 70.0),
                new TrajectoryPoint { ut = 2, latitude = -0.0971, longitude = -74.5571, altitude = 70.0 },
                Pt(3, -0.0972, -74.5572, 70.0),
            };

            List<TrajectoryPoint> walkable = SpawnCollisionDetector.ResolveWalkbackPointsOnBody(
                points, new List<TrackSection>(), "Kerbin", "test");

            Assert.Equal(3, walkable.Count);
            for (int i = 0; i < 3; i++)
                Assert.Equal(points[i].ut, walkable[i].ut);
            Assert.DoesNotContain(logLines, l => l.Contains("Walkback points resolved"));
        }

        [Fact]
        public void Wiring_BothWalkbacks_ReadOnlyTheResolvedPoints()
        {
            string eor = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "VesselSpawner.EndOfRecording.cs")));
            string eorWalk = ExtractMethodBody(eor, "internal static bool TryWalkbackForEndOfRecordingSpawn(");
            Assert.Contains("SpawnCollisionDetector.ResolveWalkbackPointsOnBody(", eorWalk);
            Assert.DoesNotContain("WalkbackAlongTrajectorySubdivided(\n                rec.Points", eorWalk.Replace("\r", ""));
            string collisions = ExtractMethodBody(eor, "Vector3d pos) CheckSpawnCollisions(");
            Assert.Contains("SpawnCollisionDetector.ResolveWalkbackPointsOnBody(", collisions);

            string ghoster = StripLineComments(File.ReadAllText(ParsekSource("Ghost", "VesselGhoster.cs")));
            string chainWalk = ExtractMethodBody(ghoster, "private uint TryWalkbackSpawn(");
            Assert.Contains("SpawnCollisionDetector.ResolveWalkbackPointsOnBody(", chainWalk);
            Assert.DoesNotContain("tipRecording.Points,\n                    walkbackBody.Radius", chainWalk.Replace("\r", ""));
            Assert.Contains("walkPt = walkPoints[validIdx];", chainWalk);
        }

        // ---- S1: every Splashed spawn is floored to sea level, the walkback included ----

        [Theory]
        [InlineData(-4.5)]
        [InlineData(2.0)]
        public void SplashedClamp_FloorsAnyNonZeroAltitudeToSeaLevel(double recordedAlt)
        {
            double alt = VesselSpawner.ClampSplashedSpawnAltitude(recordedAlt, 7, "Sub", "walkback");

            Assert.Equal(0.0, alt);
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Clamped altitude for SPLASHED spawn #7 (Sub)")
                && l.Contains("site=walkback"));
        }

        [Fact]
        public void SplashedClamp_SeaLevelIsUnchangedAndSilent()
        {
            Assert.Equal(0.0, VesselSpawner.ClampSplashedSpawnAltitude(0.0, 7, "Boat", "endpoint"));
            Assert.DoesNotContain(logLines, l => l.Contains("Clamped altitude for SPLASHED"));
        }

        [Fact]
        public void Wiring_WalkbackAndEndpoint_ShareTheSplashedClamp()
        {
            string eor = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "VesselSpawner.EndOfRecording.cs")));
            string walk = ExtractMethodBody(eor, "internal static bool TryWalkbackForEndOfRecordingSpawn(");
            string endpoint = ExtractMethodBody(eor, "internal static void ResolveSpawnPosition(");
            Assert.Contains("ClampSplashedSpawnAltitude(walkAlt,", walk);
            Assert.DoesNotContain("walkAlt > 0", walk);
            Assert.Contains("ClampSplashedSpawnAltitude(alt,", endpoint);
        }

        // ---- S2: a sea spawn is seen as over water and written SPLASHED ----

        // Stock CelestialBody.TerrainAltitude(lat, lon, allowNegative = false): a negative
        // PQS height is clamped to 0 unless allowNegative.
        private static Func<double, double, bool, double> StockTerrain(double height)
        {
            return (lat, lon, allowNegative) => !allowNegative && height < 0 ? 0.0 : height;
        }

        [Fact]
        public void OverWater_SeaFloorBelowSeaLevel_IsOverWater()
        {
            Assert.True(VesselSpawner.IsSpawnOverWater(true, -0.5, -80.0, StockTerrain(-120.0)));
        }

        [Fact]
        public void OverWater_LandOrNoOcean_IsNotOverWater()
        {
            Assert.False(VesselSpawner.IsSpawnOverWater(true, -0.09, -74.5, StockTerrain(70.0)));
            Assert.False(VesselSpawner.IsSpawnOverWater(false, -0.5, -80.0, StockTerrain(-120.0)));
            Assert.False(VesselSpawner.IsSpawnOverWater(true, -0.5, -80.0, null));
        }

        [Fact]
        public void SplashedTerminalAtSeaLevel_IsWrittenSplashed()
        {
            bool overWater = VesselSpawner.IsSpawnOverWater(true, -0.5, -80.0, StockTerrain(-120.0));
            var node = new ConfigNode("VESSEL");

            string sit = VesselSpawner.PrepareSpawnNodeAtPosition(
                node, "Kerbin", null,
                -0.5, -80.0, 0.0, 0.0, 2200.0,
                overWater, TerminalState.Splashed, null);

            Assert.Equal("SPLASHED", sit);
            Assert.Equal("SPLASHED", node.GetValue("sit"));
        }

        [Fact]
        public void Wiring_SpawnAtPosition_ReadsOverWaterThroughTheSeam()
        {
            string spawner = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "VesselSpawner.cs")));
            string body = ExtractMethodBody(spawner, "public static uint SpawnAtPosition(");
            Assert.Contains("IsSpawnOverWater(body.ocean, lat, lon, body.TerrainAltitude)", body);
            Assert.DoesNotContain("TerrainAltitude(lat, lon) < 0", body);
        }

        // ---- H: the chain tip follows the end-of-recording dead-crew and orbit rules ----

        [Fact]
        public void ChainTipDeadCrew_AllDead_SettlesTheTipAbandoned()
        {
            var tip = new Recording { RecordingId = "tip-dead-crew", VesselName = "Chain Pod" };

            bool abandoned = VesselSpawner.ApplyChainTipDeadCrewAbandon(
                tip, allCrewDead: true, "total=1 strictlyDead=1", "chain tip 'tip-dead-crew'");

            Assert.True(abandoned);
            Assert.True(tip.VesselSpawned);
            Assert.True(tip.SpawnAbandoned);
            Assert.Equal(0u, tip.SpawnedVesselPersistentId);
            Assert.Equal(SpawnWarpOutcomeWatch.ReasonNoLivingCrew,
                SpawnWarpOutcomeWatch.GetAbandonNote("tip-dead-crew"));
            Assert.Contains(logLines, l => l.Contains("[WARN][Spawner]")
                && l.Contains("Chain tip spawn ABANDONED")
                && l.Contains("no spawnable crew - total=1 strictlyDead=1"));
        }

        [Fact]
        public void ChainTipDeadCrew_LivingCrewOrEvaTip_DoesNotAbandon()
        {
            var living = new Recording { RecordingId = "tip-living", VesselName = "Chain Pod" };
            Assert.False(VesselSpawner.ApplyChainTipDeadCrewAbandon(living, false, null, "ctx"));
            Assert.False(living.SpawnAbandoned);
            Assert.False(living.VesselSpawned);

            var eva = new Recording { RecordingId = "tip-eva", VesselName = "Jeb", EvaCrewName = "Jebediah Kerman" };
            Assert.False(VesselSpawner.ApplyChainTipDeadCrewAbandon(eva, true, "total=1", "ctx"));
            Assert.False(eva.SpawnAbandoned);
            Assert.Null(SpawnWarpOutcomeWatch.GetAbandonNote("tip-eva"));
        }

        [Fact]
        public void Wiring_ChainTipSpawn_RunsTheDeadCrewGuardBeforeSpawning()
        {
            string ghoster = StripLineComments(File.ReadAllText(ParsekSource("Ghost", "VesselGhoster.cs")));
            string body = ExtractMethodBody(ghoster, "private uint SpawnChainTipWithResolvedState(");
            int guard = body.IndexOf("VesselSpawner.TryAbandonChainTipForDeadCrew(", StringComparison.Ordinal);
            int spawnAtPosition = body.IndexOf("VesselSpawner.SpawnAtPosition(", StringComparison.Ordinal);
            int respawn = body.IndexOf("VesselSpawner.RespawnVessel(", StringComparison.Ordinal);
            Assert.True(guard >= 0, "SpawnChainTipWithResolvedState does not run the dead-crew guard");
            Assert.True(guard < spawnAtPosition && guard < respawn,
                "the dead-crew guard must run before either spawn call");
        }

        [Fact]
        public void Wiring_ChainTipSpawn_CollisionChecksAnOrbitalTipAtItsPropagatedPosition()
        {
            string ghoster = StripLineComments(File.ReadAllText(ParsekSource("Ghost", "VesselGhoster.cs")));
            string body = ExtractMethodBody(ghoster, "internal uint SpawnAtChainTip(");
            int resolve = body.IndexOf("VesselSpawner.TryResolveRecordedTerminalOrbitSpawnState(", StringComparison.Ordinal);
            int check = body.IndexOf("SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(", StringComparison.Ordinal);
            Assert.True(resolve >= 0 && check >= 0);
            Assert.True(resolve < check,
                "SpawnAtChainTip must resolve the propagated orbit state before the collision check");
            Assert.Contains("haveOrbitalSpawnState\n                ? spawnBody.GetWorldSurfacePosition(orbitLat, orbitLon, orbitAlt)",
                body.Replace("\r", ""));
        }

        // ---- source helpers ----

        private static string ParsekSource(string folder, string file)
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", folder, file));
        }

        private static string StripLineComments(string source)
        {
            return Regex.Replace(source, @"//[^\r\n]*", string.Empty);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found");
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException(signature + " body is unterminated");
        }
    }
}
