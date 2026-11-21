using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Orbit-route spawn clearance (owner ruling 2026-11-21, todo
    /// SPAWN-ORBITAL-WALKBACK-NOT-WHERE-IT-SPAWNS / SPAWN-RESTING-ON-ROCK): the pure search
    /// schedule, mean-anomaly shift, push-out geometry and blocked outcome, plus the wiring that
    /// keeps every orbit-route spawn site off the body-fixed trajectory walkback. The live
    /// placement (a real phase shift and a real rock push-out) is the in-game
    /// <c>OrbitSpawnClearance</c> category.
    /// </summary>
    [Collection("Sequential")]
    public class OrbitSpawnClearanceTests : IDisposable
    {
        private const double KerbinMu = 3.5316e12;
        private readonly List<string> logLines = new List<string>();

        public OrbitSpawnClearanceTests()
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

        // ---- the phase search schedule ----

        [Fact]
        public void Schedule_Default_IsNearestFirstAheadBeforeBehindAndEndsAtTheBound()
        {
            List<double> schedule = OrbitSpawnClearance.BuildPhaseShiftScheduleMeters(
                OrbitSpawnClearance.PhaseShiftFirstStepMeters,
                OrbitSpawnClearance.PhaseShiftGrowthFactor,
                OrbitSpawnClearance.PhaseShiftMaxMeters);

            Assert.Equal(4.0, schedule[0], 9);
            Assert.Equal(-4.0, schedule[1], 9);
            Assert.Equal(6.0, schedule[2], 9);
            Assert.Equal(-6.0, schedule[3], 9);
            Assert.Equal(OrbitSpawnClearance.PhaseShiftMaxMeters, schedule[schedule.Count - 2], 9);
            Assert.Equal(-OrbitSpawnClearance.PhaseShiftMaxMeters, schedule[schedule.Count - 1], 9);
            Assert.Equal(0, schedule.Count % 2);
            for (int i = 2; i < schedule.Count; i += 2)
            {
                Assert.True(schedule[i] > schedule[i - 2], "magnitudes grow");
                Assert.Equal(-schedule[i], schedule[i + 1], 9);
            }
            // 4 * 1.5^n for n = 0..13 (4 .. 778.5 m), then the 1000 m bound: 15 magnitudes, 30 candidates.
            Assert.Equal(30, schedule.Count);
        }

        [Theory]
        [InlineData(0.0, 1.5, 1000.0)]
        [InlineData(-4.0, 1.5, 1000.0)]
        [InlineData(4.0, 1.5, 0.0)]
        [InlineData(double.NaN, 1.5, 1000.0)]
        [InlineData(4.0, 1.5, double.PositiveInfinity)]
        public void Schedule_DegenerateInputs_IsEmpty(double first, double growth, double max)
        {
            Assert.Empty(OrbitSpawnClearance.BuildPhaseShiftScheduleMeters(first, growth, max));
        }

        [Fact]
        public void Schedule_GrowthAtOrBelowOne_DoublesAndStillTerminates()
        {
            List<double> schedule = OrbitSpawnClearance.BuildPhaseShiftScheduleMeters(10.0, 1.0, 50.0);
            Assert.Equal(new[] { 10.0, -10.0, 20.0, -20.0, 40.0, -40.0, 50.0, -50.0 }, schedule.ToArray());
        }

        [Fact]
        public void Schedule_FirstStepAboveTheBound_IsClampedToOnePair()
        {
            Assert.Equal(new[] { 5.0, -5.0 },
                OrbitSpawnClearance.BuildPhaseShiftScheduleMeters(9.0, 1.5, 5.0).ToArray());
        }

        // ---- the mean-anomaly shift ----

        [Fact]
        public void ShiftedMeanAnomaly_LowKerbinOrbit_MovesByTheTimeTheDistanceTakes()
        {
            double sma = 700000.0;
            double speed = Math.Sqrt(KerbinMu / sma);
            double n = Math.Sqrt(KerbinMu / (sma * sma * sma));

            Assert.True(OrbitSpawnClearance.TryComputeShiftedMeanAnomaly(
                1.0, 0.0, sma, KerbinMu, speed, 100.0, out double shifted, out double seconds));

            Assert.Equal(100.0 / speed, seconds, 12);
            Assert.Equal(1.0 + n * seconds, shifted, 12);
            // On a circular orbit the angle swept is distance over radius.
            Assert.Equal(100.0 / sma, shifted - 1.0, 9);
        }

        [Fact]
        public void ShiftedMeanAnomaly_BehindThePhaseOrigin_WrapsIntoZeroToTwoPi()
        {
            double sma = 700000.0;
            double speed = Math.Sqrt(KerbinMu / sma);

            Assert.True(OrbitSpawnClearance.TryComputeShiftedMeanAnomaly(
                0.0, 0.01, sma, KerbinMu, speed, -50.0, out double shifted, out double seconds));

            Assert.True(seconds < 0.0);
            Assert.True(shifted > Math.PI && shifted < 2.0 * Math.PI);
            Assert.Equal(2.0 * Math.PI - 50.0 / sma, shifted, 6);
        }

        [Fact]
        public void ShiftedMeanAnomaly_OpenConic_GrowsWithoutWrapping()
        {
            double sma = -2.0e6;
            double speed = 4000.0;
            double n = Math.Sqrt(KerbinMu / (2.0e6 * 2.0e6 * 2.0e6));

            Assert.True(OrbitSpawnClearance.TryComputeShiftedMeanAnomaly(
                -0.2, 1.3, sma, KerbinMu, speed, -400.0, out double shifted, out double seconds));

            Assert.Equal(-0.1, seconds, 12);
            Assert.Equal(-0.2 - n * 0.1, shifted, 12);
        }

        [Theory]
        [InlineData(0.0, 700000.0)]
        [InlineData(-1.0, 700000.0)]
        [InlineData(2000.0, 0.0)]
        [InlineData(double.NaN, 700000.0)]
        public void ShiftedMeanAnomaly_DegenerateOrbit_Refuses(double speed, double sma)
        {
            Assert.False(OrbitSpawnClearance.TryComputeShiftedMeanAnomaly(
                1.0, 0.0, sma, KerbinMu, speed, 10.0, out double shifted, out _));
            Assert.Equal(1.0, shifted);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(7.0, 7.0 - 2.0 * Math.PI)]
        [InlineData(-0.5, 2.0 * Math.PI - 0.5)]
        [InlineData(4.0 * Math.PI + 1.0, 1.0)]
        public void WrapClosedOrbitMeanAnomaly_LandsInZeroToTwoPi(double input, double expected)
        {
            Assert.Equal(expected, OrbitSpawnClearance.WrapClosedOrbitMeanAnomaly(input), 9);
        }

        // ---- the searches ----

        [Fact]
        public void SearchPhaseShift_ReturnsTheFirstClearCandidateAndCountsTries()
        {
            var tried = new List<double>();
            OrbitSpawnClearanceSearch search = OrbitSpawnClearance.SearchPhaseShift(
                new[] { 4.0, -4.0, 6.0, -6.0 }, shift => { tried.Add(shift); return shift == -6.0 || shift == 6.0; });

            Assert.True(search.Found);
            Assert.Equal(6.0, search.Value);
            Assert.Equal(3, search.Attempts);
            Assert.Equal(new[] { 4.0, -4.0, 6.0 }, tried.ToArray());
        }

        [Fact]
        public void SearchPhaseShift_NothingClear_IsNotFoundAfterEveryCandidate()
        {
            OrbitSpawnClearanceSearch search = OrbitSpawnClearance.SearchPhaseShift(
                new[] { 4.0, -4.0 }, _ => false);
            Assert.False(search.Found);
            Assert.Equal(2, search.Attempts);

            Assert.False(OrbitSpawnClearance.SearchPhaseShift(null, _ => true).Found);
            Assert.False(OrbitSpawnClearance.SearchPhaseShift(new[] { 1.0 }, null).Found);
        }

        [Fact]
        public void SearchPushOut_StepsOutwardToTheFirstClearDistance()
        {
            OrbitSpawnClearanceSearch search = OrbitSpawnClearance.SearchPushOutDistance(
                10.0, 0.5, 40.0, d => d >= 12.3);

            Assert.True(search.Found);
            Assert.Equal(12.5, search.Value, 9);
            Assert.Equal(5, search.Attempts);
        }

        [Fact]
        public void SearchPushOut_LastCandidateIsTheBoundItself()
        {
            var tried = new List<double>();
            OrbitSpawnClearanceSearch search = OrbitSpawnClearance.SearchPushOutDistance(
                10.0, 0.75, 11.0, d => { tried.Add(d); return d >= 11.0; });

            Assert.True(search.Found);
            Assert.Equal(11.0, search.Value, 9);
            Assert.Equal(new[] { 10.75, 11.0 }, tried.ToArray());

            OrbitSpawnClearanceSearch never = OrbitSpawnClearance.SearchPushOutDistance(
                10.0, 0.5, 12.0, _ => false);
            Assert.False(never.Found);
            Assert.Equal(4, never.Attempts);
        }

        [Fact]
        public void SearchPushOut_StartBeyondTheBound_TriesOneStepOut()
        {
            OrbitSpawnClearanceSearch search = OrbitSpawnClearance.SearchPushOutDistance(
                20.0, 0.5, 15.0, d => true);
            Assert.True(search.Found);
            Assert.Equal(20.5, search.Value, 9);
            Assert.Equal(1, search.Attempts);
        }

        [Fact]
        public void SearchPushOut_DegenerateInputs_IsNotFound()
        {
            Assert.False(OrbitSpawnClearance.SearchPushOutDistance(1.0, 0.0, 10.0, _ => true).Found);
            Assert.False(OrbitSpawnClearance.SearchPushOutDistance(double.NaN, 0.5, 10.0, _ => true).Found);
            Assert.False(OrbitSpawnClearance.SearchPushOutDistance(1.0, 0.5, double.PositiveInfinity, _ => true).Found);
            Assert.False(OrbitSpawnClearance.SearchPushOutDistance(1.0, 0.5, 10.0, null).Found);
        }

        // ---- push-out geometry ----

        [Fact]
        public void BoundsRadius_IsCentreDistancePlusHalfDiagonal()
        {
            var bounds = new Bounds(new Vector3(0f, 3f, 4f), new Vector3(2f, 4f, 4f));
            Assert.Equal(5.0 + 3.0, OrbitSpawnClearance.ResolveBoundsRadius(bounds), 5);
        }

        [Fact]
        public void RockClearanceDistance_AddsRockBoundsAndMargin_IgnoringNegatives()
        {
            Assert.Equal(25.0 + 4.0 + 1.0, OrbitSpawnClearance.ResolveRockClearanceDistance(25.0, 4.0, 1.0), 9);
            Assert.Equal(4.0, OrbitSpawnClearance.ResolveRockClearanceDistance(-3.0, 4.0, -1.0), 9);
        }

        [Fact]
        public void PushOutDirection_IsStraightOutFromTheRockCentreThroughTheSpawn()
        {
            Vector3d dir = OrbitSpawnClearance.ResolvePushOutDirection(
                new Vector3d(100, 0, 0), new Vector3d(103, 4, 0), new Vector3d(0, 0, 1));
            Assert.Equal(0.6, dir.x, 9);
            Assert.Equal(0.8, dir.y, 9);
            Assert.Equal(0.0, dir.z, 9);
        }

        [Fact]
        public void PushOutDirection_SpawnAtTheCentre_TakesTheFallback_OrZero()
        {
            Vector3d fallback = OrbitSpawnClearance.ResolvePushOutDirection(
                new Vector3d(5, 5, 5), new Vector3d(5, 5, 5.001), new Vector3d(0, 0, 7));
            Assert.Equal(1.0, fallback.z, 9);

            Vector3d none = OrbitSpawnClearance.ResolvePushOutDirection(
                Vector3d.zero, Vector3d.zero, Vector3d.zero);
            Assert.Equal(0.0, none.magnitude, 9);
        }

        // ---- the blocked outcome ----

        [Fact]
        public void BlockedOrbitSpawn_RetriesThenAbandonsAtTheLimit_NeverSpawning()
        {
            var rec = new Recording { RecordingId = "orbit-blocked", VesselName = "Station Visitor" };

            for (int i = 1; i < VesselSpawner.MaxCollisionBlocks; i++)
            {
                var r = VesselSpawner.BlockOrbitRouteSpawn(rec, 3, "Station", 1, 2, 3, Vector3d.zero);
                Assert.True(r.blocked);
                Assert.False(rec.SpawnAbandoned);
                Assert.False(rec.VesselSpawned);
            }
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Spawn blocked for #3 (Station Visitor): overlaps 'Station' (orbit spawn, no clear spot within 1000m along its orbit)"));

            var last = VesselSpawner.BlockOrbitRouteSpawn(rec, 3, "Station", 1, 2, 3, Vector3d.zero);
            Assert.True(last.blocked);
            Assert.True(rec.SpawnAbandoned);
            Assert.True(rec.VesselSpawned);
            Assert.Contains(logLines, l => l.Contains("[WARN][Spawner]")
                && l.Contains("Spawn ABANDONED for #3 (Station Visitor): orbit spawn collision-blocked for 150 consecutive frames by 'Station'"));
        }

        // ---- wiring: every orbit-route spawn site uses the clearance, never the walkback ----

        [Fact]
        public void Wiring_EndOfRecording_OrbitRouteSkipsTheWalkbackAndReRunsTheSafetyCheck()
        {
            string eor = Source("Controllers", "VesselSpawner.EndOfRecording.cs");
            string spawn = Body(eor, "public static void SpawnOrRecoverIfTooClose(\n            Recording rec,");
            Assert.Contains("ref orbitalSpawnOrbit, ref orbitalSpawnVelocity", spawn);

            string resolve = Body(eor, "Vector3d pos) ResolveEndOfRecordingSpawnCollision(");
            int noOrbit = resolve.IndexOf("if (orbit == null)", StringComparison.Ordinal);
            int rock = resolve.IndexOf("SpawnCollisionDetector.TryResolveRockAtOrbitSpawn(", StringComparison.Ordinal);
            int changed = resolve.IndexOf("route.Clearance.StateChanged", StringComparison.Ordinal);
            int safety = resolve.IndexOf("TryPassTerminalOrbitSpawnSafety(rec, index, body, orbit, spawnUT,", StringComparison.Ordinal);
            Assert.True(noOrbit >= 0 && noOrbit < rock && rock < changed && changed < safety,
                "the orbit route checks the rock first and re-runs the safety check on a moved orbit");

            string check = Body(eor, "Vector3d pos) CheckSpawnCollisions(");
            int duplicate = check.IndexOf("ShouldEnterDuplicateBlockerRecovery(", StringComparison.Ordinal);
            int orbitBranch = check.IndexOf("if (orbitRoute != null)", StringComparison.Ordinal);
            int walk = check.IndexOf("SpawnCollisionDetector.ResolveWalkbackPointsOnBody(", StringComparison.Ordinal);
            Assert.True(duplicate >= 0 && duplicate < orbitBranch && orbitBranch < walk,
                "an orbit-route overlap keeps the duplicate recovery and leaves before the trajectory walkback");

            string overlap = Body(eor, "Vector3d pos) ResolveOrbitRouteOverlap(");
            Assert.Contains("SpawnCollisionDetector.SearchClearOrbitPhase(", overlap);
            Assert.Contains("BlockOrbitRouteSpawn(", overlap);
        }

        [Fact]
        public void Wiring_ChainTip_BothSitesUseTheOrbitClearanceBeforeTheSafetyCheck()
        {
            string ghoster = Source("Ghost", "VesselGhoster.cs");
            string tip = Body(ghoster, "internal uint SpawnAtChainTip(");
            int clearance = tip.IndexOf("ResolveChainTipSpawnOverlap(", StringComparison.Ordinal);
            int safety = tip.IndexOf("PassChainTipTerminalOrbitSafety(", StringComparison.Ordinal);
            Assert.True(clearance >= 0 && clearance < safety,
                "the first chain-tip attempt clears an orbital tip before its safety check");
            string firstOverlap = Body(ghoster, "Vessel blocker) ResolveChainTipSpawnOverlap(");
            int noOrbit = firstOverlap.IndexOf("if (!orbital)", StringComparison.Ordinal);
            int plain = firstOverlap.IndexOf("SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels(", StringComparison.Ordinal);
            int orbitClear = firstOverlap.IndexOf("ApplyChainTipOrbitClearance(", StringComparison.Ordinal);
            Assert.True(noOrbit >= 0 && noOrbit < plain && plain < orbitClear,
                "a tip with no orbit keeps the plain overlap check; an orbital tip takes the clearance");

            string blocked = Body(ghoster, "internal uint TrySpawnBlockedChain(");
            int orbitTip = blocked.IndexOf("TrySpawnBlockedOrbitChainTip(", StringComparison.Ordinal);
            int extender = blocked.IndexOf("ComputePropagatedPosition(", StringComparison.Ordinal);
            int walkback = blocked.IndexOf("TryWalkbackSpawn(", StringComparison.Ordinal);
            Assert.True(orbitTip >= 0 && orbitTip < extender && extender < walkback,
                "a blocked orbital tip is retried at its orbit before the ghost-extender recheck and the walkback");

            string orbitRetry = Body(ghoster, "private bool TrySpawnBlockedOrbitChainTip(");
            Assert.Contains("ApplyChainTipOrbitClearance(", orbitRetry);
            Assert.Contains("PassChainTipTerminalOrbitSafety(", orbitRetry);
            Assert.DoesNotContain("TryWalkbackSpawn(", orbitRetry);
        }

        [Fact]
        public void Wiring_RocksAreBlockersOnlyThroughTheOrbitClearance()
        {
            // The ordinary overlap still skips a SpaceObject (a landed spawn is unchanged); the
            // orbit route finds rocks through its own scan.
            Assert.Equal(SpawnCollisionDetector.LoadedVesselHitVerdict.SkippedVesselType,
                SpawnCollisionDetector.ClassifyLoadedVesselHit(true, false, 5u, 0u, VesselType.SpaceObject));

            string clearance = Source("Ghost", "SpawnCollisionDetector.OrbitClearance.cs");
            string phase = Body(clearance, "internal static OrbitSpawnClearanceResult SearchClearOrbitPhase(");
            Assert.Contains("TryFindRockBlocker(candidatePos,", phase);
            string full = Body(clearance, "internal static OrbitSpawnClearanceResult ResolveOrbitSpawnClearance(");
            int rock = full.IndexOf("TryResolveRockAtOrbitSpawn(", StringComparison.Ordinal);
            int overlap = full.IndexOf("CheckOverlapAgainstLoadedVessels(", StringComparison.Ordinal);
            Assert.True(rock >= 0 && rock < overlap, "a rock is pushed out from before any phase search");
            string push = Body(clearance, "internal static OrbitSpawnClearanceResult ResolveRockPushOut(");
            Assert.Contains("rock.Vessel.orbit.getOrbitalVelocityAtUT(spawnUT)", push);
            Assert.Contains("OrbitSpawnClearance.TryBuildCoMovingOrbit(", push);
            // A Klaw-held rock is part of a Ship: the final check at the spawn padding must not
            // count the rock the push-out just cleared by its smaller margin.
            Assert.Contains("ignorePart: rock.Part", push);
            string detector = Source("Ghost", "SpawnCollisionDetector.cs");
            string check = Body(detector, "Vessel blockerVessel) CheckOverlapAgainstLoadedVessels(");
            Assert.Contains("isIgnoredPart: ignorePart != null && part == ignorePart", check);
            Assert.Contains("verdict == LoadedVesselHitVerdict.IgnoredPart", check);
        }

        [Fact]
        public void ClassifyLoadedVesselHit_TheIgnoredRockPartOfAHoldingShip_DoesNotBlock()
        {
            Assert.Equal(SpawnCollisionDetector.LoadedVesselHitVerdict.IgnoredPart,
                SpawnCollisionDetector.ClassifyLoadedVesselHit(true, false, 7u, 0u, VesselType.Ship, isIgnoredPart: true));
            // Every other part of the holding ship still blocks.
            Assert.Equal(SpawnCollisionDetector.LoadedVesselHitVerdict.Blocker,
                SpawnCollisionDetector.ClassifyLoadedVesselHit(true, false, 7u, 0u, VesselType.Ship));
            Assert.Equal(SpawnCollisionDetector.LoadedVesselHitVerdict.NotLoaded,
                SpawnCollisionDetector.ClassifyLoadedVesselHit(false, false, 7u, 0u, VesselType.Ship, isIgnoredPart: true));
        }

        // ---- source helpers ----

        private static string Source(string folder, string file)
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", folder, file));
            return Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", string.Empty).Replace("\r", "");
        }

        private static string Body(string source, string signature)
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
