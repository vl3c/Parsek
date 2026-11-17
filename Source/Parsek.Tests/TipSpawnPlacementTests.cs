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
    /// lat/lon), a Splashed spawn keeps its recorded depth on every path (never into the sea
    /// floor; owner ruling 2026-11-17), a sea spawn is
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
        public void Walkback_ResolvedSummary_IsRateLimitedPerRecording()
        {
            var (points, sections) = LanderNearBase(withShadow: false);

            for (int i = 0; i < 5; i++)
                SpawnCollisionDetector.ResolveWalkbackPointsOnBody(points, sections, "Kerbin", "rec-a");
            SpawnCollisionDetector.ResolveWalkbackPointsOnBody(points, sections, "Kerbin", "rec-b");

            Assert.Single(logLines.FindAll(l => l.Contains("Walkback points resolved for rec-a")));
            Assert.Single(logLines.FindAll(l => l.Contains("Walkback points resolved for rec-b")));
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

        // ---- Splashed spawns keep their recorded depth (owner ruling 2026-11-17), on every path ----

        [Fact]
        public void SplashedDepth_RecordedDepthAboveTheSeaFloor_IsKept()
        {
            double alt = VesselSpawner.ResolveSplashedSpawnAltitude(-50.0, -400.0, 7, "Sub", "walkback");

            Assert.Equal(-50.0, alt);
            Assert.DoesNotContain(logLines, l => l.Contains("Clamped altitude for SPLASHED"));
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Splashed spawn #7 (Sub) keeps its recorded depth alt=-50.0 (seaFloor=-400.0, site=walkback)"));
        }

        [Fact]
        public void SplashedDepth_AboveSeaLevel_GoesToZero()
        {
            Assert.Equal(0.0, VesselSpawner.ResolveSplashedSpawnAltitude(2.0, -400.0, 7, "Boat", "endpoint"));
            Assert.Contains(logLines, l => l.Contains("Clamped altitude for SPLASHED spawn #7 (Boat): 2.0 -> 0 (above-sea-level, site=endpoint)"));
        }

        [Theory]
        [InlineData(-50.0, -30.0, -28.0)]   // a shallower spot: raised to the floor plus 2 m
        [InlineData(-50.0, -1.0, 0.0)]      // a floor within 2 m of the surface: never above sea level
        [InlineData(-3.0, 5.0, 0.0)]        // dry land under the spot: sea level
        public void SplashedDepth_IntoTheSeaFloor_IsRaisedAboveIt(double alt, double seaFloor, double expected)
        {
            Assert.Equal(expected, VesselSpawner.ResolveSplashedSpawnAltitude(alt, seaFloor, 8, "Sub", "de-overlap"), 6);
            Assert.Contains(logLines, l => l.Contains("Clamped altitude for SPLASHED spawn #8 (Sub)")
                && l.Contains("below-sea-floor"));
        }

        [Fact]
        public void SplashedDepth_SeaLevelOrUnknownFloor_IsUnchanged()
        {
            Assert.Equal(0.0, VesselSpawner.ResolveSplashedSpawnAltitude(0.0, -400.0, 7, "Boat", "endpoint"));
            Assert.Equal(-12.0, VesselSpawner.ResolveSplashedSpawnAltitude(-12.0, double.NaN, 7, "Sub", "endpoint"));
            Assert.Equal(0.0, VesselSpawner.ResolveSplashedSpawnAltitude(double.NaN, -400.0, 7, "Sub", "endpoint"));
            Assert.DoesNotContain(logLines, l => l.Contains("below-sea-floor"));
        }

        [Fact]
        public void Wiring_EverySplashedSpawnPath_SharesTheDepthRuleWithTheSeaFloorUnderIt()
        {
            string eor = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "VesselSpawner.EndOfRecording.cs")));
            string walk = ExtractMethodBody(eor, "internal static bool TryWalkbackForEndOfRecordingSpawn(");
            string endpoint = ExtractMethodBody(eor, "internal static void ResolveSpawnPosition(");
            string refloor = ExtractMethodBody(eor, "internal static double ReFloorNudgedSurfaceSpawnAltitude(");
            Assert.Contains("ResolveSplashedSpawnAltitude(walkAlt,\n                        body.TerrainAltitude(walkLat, walkLon, true)",
                walk.Replace("\r", ""));
            Assert.DoesNotContain("walkAlt > 0", walk);
            Assert.Contains("TryReadSeaFloorAltitude(endpointBodyName, lat, lon)", endpoint);
            Assert.Contains("ResolveSplashedSpawnAltitude(alt, seaFloor,", endpoint);
            Assert.Contains("ResolveSplashedSpawnAltitude(alt, body.TerrainAltitude(lat, lon, true),", refloor);
            Assert.DoesNotContain("ClampSplashedSpawnAltitude", eor);

            string helper = ExtractMethodBody(eor, "private static void ApplyEndOfRecordingLandedDeOverlap(");
            Assert.Contains("ReFloorNudgedSurfaceSpawnAltitude(", helper);
            string ksc = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "ParsekKSC.cs")));
            Assert.Contains("VesselSpawner.ReFloorNudgedSurfaceSpawnAltitude(", ksc);
        }

        [Fact]
        public void ChainTipSplashedAltitude_SurfaceSpawnKeepsItsRecordedDepth()
        {
            var splashed = new Recording { RecordingId = "tip-sub", VesselName = "Sub", TerminalStateValue = TerminalState.Splashed };
            var landed = new Recording { RecordingId = "tip-rover", VesselName = "Rover", TerminalStateValue = TerminalState.Landed };

            Assert.Equal(-3.5, VesselGhoster.ResolveChainTipSurfaceSpawnAltitude(splashed, false, -3.5, -40.0, "walkback chain tip"));
            Assert.Equal(-18.0, VesselGhoster.ResolveChainTipSurfaceSpawnAltitude(splashed, false, -45.0, -20.0, "walkback chain tip"));
            Assert.Equal(0.0, VesselGhoster.ResolveChainTipSurfaceSpawnAltitude(splashed, false, 1.5, -40.0, "ctx"));
            Assert.Equal(-60.0, VesselGhoster.ResolveChainTipSurfaceSpawnAltitude(splashed, true, -60.0, -20.0, "ctx"));
            Assert.Equal(-3.5, VesselGhoster.ResolveChainTipSurfaceSpawnAltitude(landed, false, -3.5, -40.0, "ctx"));
            Assert.Contains(logLines, l => l.Contains("Clamped altitude for SPLASHED spawn #-1 (Sub): -45.0 -> -18.0")
                && l.Contains("site=walkback chain tip"));
        }

        [Fact]
        public void Wiring_ChainTipSpawn_FloorsASplashedTipBeforeWritingTheSnapshot()
        {
            string ghoster = StripLineComments(File.ReadAllText(ParsekSource("Ghost", "VesselGhoster.cs")));
            string body = ExtractMethodBody(ghoster, "private uint SpawnChainTipWithResolvedState(");
            int floor = body.IndexOf("body.TerrainAltitude(spawnLat, spawnLon, true)", StringComparison.Ordinal);
            int clamp = body.IndexOf("spawnAlt = ResolveChainTipSurfaceSpawnAltitude(", StringComparison.Ordinal);
            int write = body.IndexOf("VesselSpawner.ApplyResolvedSpawnStateToSnapshot(", StringComparison.Ordinal);
            Assert.True(floor >= 0 && floor < clamp && clamp < write,
                "SpawnChainTipWithResolvedState must apply the Splashed depth rule, with the sea floor under the tip, before it writes the spawn position");
        }

        [Fact]
        public void ChainTipSplashed_TerrainCorrectionNeverLiftsTheRecordedDepth()
        {
            // The chain tip's underground correction reads TerrainAltitude(lat, lon), which
            // clamps a sea floor to 0: it must never run for a Splashed tip, or a submarine
            // tip resolved to -50 m is rewritten to +2 m before RespawnVessel.
            Assert.False(TerrainCorrector.ShouldCorrectTerrain(TerminalState.Splashed, -50.0));
            Assert.True(TerrainCorrector.ShouldCorrectTerrain(TerminalState.Landed, 65.0));

            string ghoster = StripLineComments(File.ReadAllText(ParsekSource("Ghost", "VesselGhoster.cs")));
            string body = ExtractMethodBody(ghoster, "private uint SpawnChainTipWithResolvedState(");
            int gate = body.IndexOf("TerrainCorrector.ShouldCorrectTerrain(", StringComparison.Ordinal);
            int apply = body.IndexOf("ApplyTerrainCorrection(tipRecording, spawnSnapshot)", StringComparison.Ordinal);
            Assert.True(gate >= 0 && apply > gate,
                "the chain tip's terrain correction must stay behind ShouldCorrectTerrain");
            Assert.Equal(1, Regex.Matches(ghoster, @"ApplyTerrainCorrection\(").Count - 1);
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
                && l.Contains("Spawn ABANDONED for chain tip")
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
        public void ChainTipSettledWithoutVessel_OnlyForADeliberateSettle()
        {
            Assert.False(VesselGhoster.IsChainTipSettledWithoutVessel(null));
            Assert.False(VesselGhoster.IsChainTipSettledWithoutVessel(new Recording()));
            Assert.True(VesselGhoster.IsChainTipSettledWithoutVessel(
                new Recording { VesselSpawned = true, SpawnAbandoned = true }));
            Assert.False(VesselGhoster.IsChainTipSettledWithoutVessel(
                new Recording { VesselSpawned = true, SpawnAbandoned = true, SpawnedVesselPersistentId = 77u }));
            Assert.False(VesselGhoster.IsChainTipSettledWithoutVessel(
                new Recording { VesselSpawned = true }));
        }

        [Fact]
        public void EndOfRecordingDeadCrew_EvaOrNoSnapshot_IsNeverAbandoned()
        {
            var eva = new Recording { RecordingId = "eor-eva", EvaCrewName = "Jebediah Kerman", VesselSnapshot = new ConfigNode("VESSEL") };
            var noSnapshot = new Recording { RecordingId = "eor-none" };

            Assert.False(VesselSpawner.TryAbandonEndOfRecordingSpawnForDeadCrew(eva, 1));
            Assert.False(VesselSpawner.TryAbandonEndOfRecordingSpawnForDeadCrew(noSnapshot, 2));
            Assert.False(eva.SpawnAbandoned);
            Assert.False(noSnapshot.SpawnAbandoned);
        }

        [Fact]
        public void Wiring_EveryRespawnFallback_RunsTheDeadCrewGuardFirst()
        {
            string eor = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "VesselSpawner.EndOfRecording.cs")));
            string spawn = ExtractMethodBody(eor.Replace("\r", ""),
                "public static void SpawnOrRecoverIfTooClose(\n            Recording rec,");
            AssertGuardBeforeEachCall(spawn, "TryAbandonEndOfRecordingSpawnForDeadCrew(rec, index)",
                "RespawnValidatedRecording(", "SpawnOrRecoverIfTooClose");

            string ghoster = StripLineComments(File.ReadAllText(ParsekSource("Ghost", "VesselGhoster.cs")));
            foreach (string sig in new[] { "internal uint SpawnAtChainTip(", "internal uint TrySpawnBlockedChain(" })
            {
                AssertGuardBeforeEachCall(ExtractMethodBody(ghoster, sig),
                    "VesselSpawner.TryAbandonChainTipForDeadCrew(", "VesselSpawner.RespawnValidatedRecording(", sig);
            }
        }

        private static void AssertGuardBeforeEachCall(string body, string guard, string call, string where)
        {
            int count = 0;
            int windowStart = 0;
            int at = body.IndexOf(call, StringComparison.Ordinal);
            while (at >= 0)
            {
                count++;
                int g = body.IndexOf(guard, windowStart, at - windowStart, StringComparison.Ordinal);
                Assert.True(g >= 0, where + ": respawn call #" + count + " is not preceded by the dead-crew guard");
                windowStart = at + call.Length;
                at = body.IndexOf(call, windowStart, StringComparison.Ordinal);
            }
            Assert.True(count > 0, where + " has no " + call);
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

        // ---- landed de-overlap on the end-of-recording spawn (SPAWN-TS-STACK-NO-DE-OVERLAP) ----

        [Fact]
        public void EndOfRecordingDeOverlap_OnlyForANonEvaSurfaceEndAtItsEndpoint()
        {
            Assert.True(VesselSpawner.ShouldDeOverlapEndOfRecordingSpawn(false, false, TerminalState.Landed));
            Assert.True(VesselSpawner.ShouldDeOverlapEndOfRecordingSpawn(false, false, TerminalState.Splashed));
            Assert.False(VesselSpawner.ShouldDeOverlapEndOfRecordingSpawn(true, false, TerminalState.Landed));
            Assert.False(VesselSpawner.ShouldDeOverlapEndOfRecordingSpawn(false, true, TerminalState.Landed));
            Assert.False(VesselSpawner.ShouldDeOverlapEndOfRecordingSpawn(false, false, TerminalState.Orbiting));
            Assert.False(VesselSpawner.ShouldDeOverlapEndOfRecordingSpawn(false, false, null));
        }

        [Fact]
        public void EndOfRecordingDeOverlap_SecondDeliveryOnOneSpot_IsPlacedClear()
        {
            // TSS-3's spot: the first delivery already stands there as an unloaded proto.
            var existing = new List<(double lat, double lon)> { (-0.1000, -74.8300) };
            var result = SpawnCollisionDetector.ComputeDeOverlappedLandedSpawn(
                -0.1000, -74.8300, existing,
                SpawnCollisionDetector.DefaultLandedSpawnSeparationMeters, R);

            Assert.True(result.Nudged);
            Assert.False(result.Exhausted);
            Assert.True(result.NearestBlockerMeters >= 10.0,
                "nearest after the nudge was " + result.NearestBlockerMeters + " m");
        }

        [Fact]
        public void EndOfRecordingDeOverlapLine_IsInvariantAndMatchesTheLanesNudgePattern()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                var nudged = new SpawnCollisionDetector.DeOverlapResult
                {
                    Latitude = -0.099865, Longitude = -74.83, Nudged = true,
                    NudgeMeters = 15.0, NearestBlockerMeters = 15.0,
                };
                string line = VesselSpawner.FormatEndOfRecordingDeOverlapLine(
                    7, "Stack Probe B", nudged, 1, -0.1, -74.83, 66.0, 67.5);
                Assert.Contains("De-overlap for #7 \"Stack Probe B\": nudged 15.0m", line);
                Assert.Contains("site=end-of-recording, existing unloaded landed=1", line);
                Assert.Contains("lat -0.100000->-0.099865", line);
                Assert.Contains("alt 66.0->67.5", line);
                // The forbidden-nudge pattern EVA-6 / EVA-9 / EVA-10 pin covers this site too.
                Assert.Matches("De-overlap for #[0-9]+ \\\"Stack Probe B\\\": nudged", line);

                var clear = new SpawnCollisionDetector.DeOverlapResult
                {
                    Latitude = -0.1, Longitude = -74.83, NearestBlockerMeters = 42.2,
                };
                string clearLine = VesselSpawner.FormatEndOfRecordingDeOverlapLine(
                    7, "Stack Probe A", clear, 0, -0.1, -74.83, 66.0, 66.0);
                Assert.Contains("De-overlap for #7 \"Stack Probe A\": clear", clearLine);
                Assert.Contains("nearest=42.2m >= min=15m", clearLine);
                Assert.DoesNotContain("nudged", clearLine);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void Wiring_EndOfRecordingSpawn_DeOverlapsAgainstUnloadedLandedVesselsBeforeTheCollisionCheck()
        {
            string eor = StripLineComments(File.ReadAllText(
                ParsekSource("Controllers", "VesselSpawner.EndOfRecording.cs"))).Replace("\r", "");
            string spawn = ExtractMethodBody(eor,
                "public static void SpawnOrRecoverIfTooClose(\n            Recording rec,");
            int orbit = spawn.IndexOf("TryResolveRecordedTerminalOrbitSpawnState(", StringComparison.Ordinal);
            int decide = spawn.IndexOf(
                "ShouldDeOverlapEndOfRecordingSpawn(isEva, useRecordedTerminalOrbit, rec.TerminalStateValue)",
                StringComparison.Ordinal);
            int apply = spawn.IndexOf(
                "ApplyEndOfRecordingLandedDeOverlap(rec, index, body, ref spawnLat, ref spawnLon, ref spawnAlt)",
                StringComparison.Ordinal);
            int pos = spawn.IndexOf("body.GetWorldSurfacePosition(spawnLat, spawnLon, spawnAlt)", StringComparison.Ordinal);
            int check = spawn.IndexOf("CheckSpawnCollisions(", StringComparison.Ordinal);
            Assert.True(orbit >= 0 && decide >= 0 && apply >= 0 && pos >= 0 && check >= 0);
            Assert.True(orbit < decide && decide < apply && apply < pos && pos < check,
                "the de-overlap must run after the endpoint is resolved and before the spawn position and collision check");

            string helper = ExtractMethodBody(eor, "private static void ApplyEndOfRecordingLandedDeOverlap(");
            Assert.Contains("GatherExistingLandedVesselPositions(", helper);
            Assert.Contains("includeLoadedVessels: false", helper);
            Assert.Contains("SpawnCollisionDetector.ComputeDeOverlappedLandedSpawn(", helper);
            Assert.Contains("ReFloorNudgedSurfaceSpawnAltitude(rec, index, body,", helper);

            string gather = ExtractMethodBody(eor,
                "internal static List<(double lat, double lon)> GatherExistingLandedVesselPositions(");
            Assert.Contains("if (!includeLoadedVessels && v.loaded)", gather);

            // The Space Center spawn keeps every landed vessel, loaded ones included.
            string ksc = StripLineComments(File.ReadAllText(ParsekSource("Controllers", "ParsekKSC.cs")));
            Assert.Contains("VesselSpawner.GatherExistingLandedVesselPositions(", ksc);
            Assert.DoesNotContain("includeLoadedVessels", ksc);
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
