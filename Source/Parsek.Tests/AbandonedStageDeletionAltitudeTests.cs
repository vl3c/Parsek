using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner rulings Q2 / Q3 / Q5 for the predicted tail of an abandoned stage:
    /// a drag-free fall ends at ground / water impact (not at the atmosphere top); an arc whose
    /// low point is above the ground but below the deletion altitude (where the pressure falls
    /// to stock's 1 kPa packed-vessel deletion threshold) ends Destroyed at its first low point;
    /// an orbit whose low point clears the deletion altitude stays Orbiting even inside the
    /// upper atmosphere. Kerbin-like fixture: R = 600 km, atmosphere 70 km, deletion altitude
    /// 22 km (<see cref="OrbitClearanceTestValues.KerbinDeletionAltitude"/>).
    /// </summary>
    [Collection("Sequential")]
    public class AbandonedStageDeletionAltitudeTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double KerbinGravParameter = 3.5316e12;
        private const double DeletionAltitude = OrbitClearanceTestValues.KerbinDeletionAltitude;

        private const double MunRadius = 200000.0;
        private const double MunGravParameter = 6.5138398e10;
        private const double MunMaxTerrain = 7061.0;

        private readonly List<string> logLines = new List<string>();

        public AbandonedStageDeletionAltitudeTests()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- floor math (pure) ----

        // Exponential Kerbin-like pressure: 101.325 kPa at sea level, 1 kPa at 22 km.
        private static readonly double ScaleHeight = DeletionAltitude / Math.Log(101.325);

        private static double KerbinLikePressure(double altitude)
        {
            return 101.325 * Math.Exp(-altitude / ScaleHeight);
        }

        [Fact]
        public void ComputeDeletionAltitude_FindsTheOneKiloPascalLine()
        {
            double deletion = OrbitClearance.ComputeDeletionAltitude(KerbinLikePressure, KerbinAtmosphereDepth);
            Assert.InRange(deletion, DeletionAltitude - 1.0, DeletionAltitude + 1.0);
            Assert.True(KerbinLikePressure(deletion + 10.0) < OrbitClearance.StockPackedDeletionPressureKPa);
            Assert.True(KerbinLikePressure(deletion - 10.0) > OrbitClearance.StockPackedDeletionPressureKPa);
        }

        [Fact]
        public void ComputeDeletionAltitude_NoAtmosphereOrThinSurface_IsZero()
        {
            Assert.Equal(0.0, OrbitClearance.ComputeDeletionAltitude(KerbinLikePressure, 0.0));
            Assert.Equal(0.0, OrbitClearance.ComputeDeletionAltitude(KerbinLikePressure, double.NaN));
            Assert.Equal(0.0, OrbitClearance.ComputeDeletionAltitude(null, KerbinAtmosphereDepth));
            // Surface pressure at or below the threshold: nothing is ever deleted.
            Assert.Equal(0.0, OrbitClearance.ComputeDeletionAltitude(alt => 0.8, 50000.0));
            Assert.Equal(0.0, OrbitClearance.ComputeDeletionAltitude(alt => 1.0, 50000.0));
            Assert.Equal(0.0, OrbitClearance.ComputeDeletionAltitude(alt => double.NaN, 50000.0));
        }

        [Fact]
        public void ComputeDeletionAltitude_TopStillAboveThreshold_IsTheAtmosphereTop()
        {
            Assert.Equal(50000.0, OrbitClearance.ComputeDeletionAltitude(alt => 5.0, 50000.0));
        }

        [Fact]
        public void ComputeDeletionAltitude_NonFiniteSamplesMoveTheLineUp()
        {
            // A NaN band in the middle of the curve reads as above the threshold, so the
            // result sits at or above the band, never below the true line.
            double deletion = OrbitClearance.ComputeDeletionAltitude(
                alt => alt > 15000.0 && alt < 30000.0 ? double.NaN : KerbinLikePressure(alt),
                KerbinAtmosphereDepth);
            Assert.InRange(deletion, 29999.0, 30001.0);
        }

        [Fact]
        public void PeriapsisFloor_UsesTheDeletionAltitudeNotTheAtmosphereTop()
        {
            ExtrapolationBody kerbin = MakeKerbin();
            Assert.Equal(DeletionAltitude, kerbin.PeriapsisFloorAltitude);
            Assert.True(kerbin.PeriapsisFloorAltitude < kerbin.AtmosphereDepth);
        }

        [Fact]
        public void PeriapsisFloor_AirlessBody_StillUsesMaxTerrain()
        {
            ExtrapolationBody mun = MakeMun();
            Assert.Equal(MunMaxTerrain, mun.PeriapsisFloorAltitude);
            Assert.Equal(MunMaxTerrain, OrbitClearance.ComputePeriapsisFloorAltitude(0.0, MunMaxTerrain));

            // ORBITING with a periapsis under the highest terrain still extrapolates on the floor.
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.ORBITING, 0.02, 5000.0, mun.PeriapsisFloorAltitude));
            Assert.False(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.ORBITING, 0.02, MunMaxTerrain + 1000.0, mun.PeriapsisFloorAltitude));
        }

        // ---- B3 / Q2: a fall ends at the ground, not at the atmosphere top ----

        [Fact]
        public void Extrapolate_SuborbitalFrom90Km_EndsDestroyedAtSurfaceCrossing()
        {
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState(KerbinRadius + 90000.0, 1500.0),
                bodies,
                ShortLimits());

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.None, result.failureReason);
            double terminalAltitude = Altitude(result.terminalPosition, KerbinRadius);
            Assert.InRange(terminalAltitude, -1.0, 100.0);
            Assert.Single(result.segments);
            Assert.Equal(result.terminalUT, result.segments[0].endUT, 6);

            // The terminal is AFTER the 70 km crossing: the atmosphere top is not an ending.
            Assert.True(BallisticExtrapolator.TryPropagate(
                result.segments[0], KerbinGravParameter, result.terminalUT - 30.0, out Vector3d earlier, out _));
            Assert.True(Altitude(earlier, KerbinRadius) < KerbinAtmosphereDepth);
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("Terminal reason=" + BallisticExtrapolator.CutoffReasonSurfaceImpact));
        }

        // ---- B8 / Q3: low point above the ground, below the deletion altitude ----

        [Fact]
        public void Extrapolate_From60KmOn80x10KmArc_EndsDestroyedAtFirstPeriapsis()
        {
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };
            BallisticStateVector start = MakeDescendingStateOnArc(
                periapsisAltitude: 10000.0, apoapsisAltitude: 80000.0, altitude: 60000.0);
            double startAltitude = Altitude(start.position, KerbinRadius);
            Assert.InRange(startAltitude, 59999.0, 60001.0);

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.Equal(ExtrapolationFailureReason.None, result.failureReason);
            double terminalAltitude = Altitude(result.terminalPosition, KerbinRadius);
            Assert.InRange(terminalAltitude, 9990.0, 10010.0);
            // At periapsis the radial velocity is zero.
            double radialSpeed = Vector3d.Dot(result.terminalPosition.normalized, result.terminalVelocity);
            Assert.InRange(radialSpeed, -1.0, 1.0);
            // No horizon run: the descent to periapsis takes minutes, not 50 years.
            Assert.InRange(result.terminalUT - start.ut, 1.0, 1000.0);
            Assert.Single(result.segments);
            Assert.Equal(result.terminalUT, result.segments[0].endUT, 6);
            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("Terminal reason=" + BallisticExtrapolator.CutoffReasonDeletionPeriapsis));
            Assert.DoesNotContain(logLines, l => l.Contains("Terminal reason=horizon-cap"));
        }

        [Fact]
        public void Extrapolate_From60KmOn80x10KmArc_WithoutDeletionAltitude_RunsToTheHorizon()
        {
            // The mechanism of finding B8, kept as a contrast: with no deletion altitude the
            // same drag-free arc never meets the ground and only the horizon ends it.
            ExtrapolationBody kerbin = MakeKerbin();
            kerbin.DeletionAltitude = 0.0;
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = kerbin };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeDescendingStateOnArc(10000.0, 80000.0, 60000.0),
                bodies,
                ShortLimits());

            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Contains(logLines, l => l.Contains("Terminal reason=horizon-cap"));
        }

        [Fact]
        public void Extrapolate_AscendingBelowDeletionAltitude_EndsAtTheNextPeriapsisPass()
        {
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };
            BallisticStateVector start = MakeDescendingStateOnArc(10000.0, 80000.0, 60000.0);
            start.velocity = -start.velocity; // same conic, flown the other way: climbing

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.InRange(Altitude(result.terminalPosition, KerbinRadius), 9990.0, 10010.0);
        }

        // ---- Q3: low point between the deletion altitude and the atmosphere top ----

        [Fact]
        public void OrbitWithPeriapsisBetweenDeletionAltitudeAndAtmosphereTop_StaysOrbiting()
        {
            ExtrapolationBody kerbin = MakeKerbin();
            const double pe = 40000.0;
            const double ap = 100000.0;
            double sma = KerbinRadius + 0.5 * (pe + ap);
            double ecc = (ap - pe) / (2.0 * KerbinRadius + pe + ap);

            // Not extrapolated at scene exit / cache refresh: the cutoff is the floor.
            Assert.False(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.ORBITING, ecc, pe, kerbin.PeriapsisFloorAltitude));
            // Old line, for contrast: the atmosphere top would have extrapolated it.
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.ORBITING, ecc, pe, KerbinAtmosphereDepth));

            // Passes the one floor every Orbiting reader uses, and spawns.
            Assert.True(OrbitClearance.IsBoundOrbitClear(
                ecc, sma * (1.0 - ecc), KerbinRadius, kerbin.PeriapsisFloorAltitude));
            Assert.Equal(TerminalState.Orbiting, RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.ORBITING, ecc, sma * (1.0 - ecc), KerbinRadius, DeletionAltitude));
            var spawn = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true, KerbinAtmosphereDepth, ap, pe, ap, 0.0, DeletionAltitude);
            Assert.NotEqual(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, spawn.Action);

            // A captured stock conic with that periapsis is not clipped at the atmosphere.
            var segments = new List<OrbitSegment>
            {
                MakeSegment("Kerbin", sma, ecc, meanAnomalyAtEpoch: Math.PI, epoch: 0.0, startUT: 0.0, endUT: 3000.0)
            };
            Assert.False(BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = kerbin }, out _, out _));

            // And an extrapolation through it never ends it early.
            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                MakeTangentialState(KerbinRadius + ap, Math.Sqrt(KerbinGravParameter * (2.0 / (KerbinRadius + ap) - 1.0 / sma))),
                new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = kerbin },
                ShortLimits());
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
        }

        [Fact]
        public void ReentryClip_PeriapsisBelowDeletionAltitude_ClipsAtAtmosphereEntry()
        {
            ExtrapolationBody kerbin = MakeKerbin();
            const double pe = 10000.0;
            const double ap = 100000.0;
            double sma = KerbinRadius + 0.5 * (pe + ap);
            double ecc = (ap - pe) / (2.0 * KerbinRadius + pe + ap);
            var segments = new List<OrbitSegment>
            {
                MakeSegment("Kerbin", sma, ecc, meanAnomalyAtEpoch: Math.PI, epoch: 0.0, startUT: 0.0, endUT: 3000.0)
            };

            Assert.True(BallisticExtrapolator.TryFindAtmosphericReentryClip(
                segments, new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = kerbin },
                out int clipIndex, out double entryUT));
            Assert.Equal(0, clipIndex);
            Assert.True(BallisticExtrapolator.TryPropagate(segments[0], KerbinGravParameter, entryUT, out Vector3d pos, out _));
            Assert.InRange(Altitude(pos, KerbinRadius), KerbinAtmosphereDepth - 50.0, KerbinAtmosphereDepth + 50.0);
        }

        // ---- B5: the on-rails refresh checks the floor ----

        [Fact]
        public void OnRailsSegment_PeriapsisBelowFloor_CachesDestroyedWithPredictedTail()
        {
            const double pe = 10000.0;
            const double ap = 80000.0;
            double sma = KerbinRadius + 0.5 * (pe + ap);
            double ecc = (ap - pe) / (2.0 * KerbinRadius + pe + ap);
            OrbitSegment segment = MakeSegment("Kerbin", sma, ecc, meanAnomalyAtEpoch: Math.PI, epoch: 0.0,
                startUT: 0.0, endUT: 0.0);

            bool built = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-onrails-graze",
                77u,
                segment,
                0.0,
                FinalizationCacheOwner.BackgroundOnRails,
                "unit",
                out RecordingFinalizationCache cache,
                new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() });

            Assert.True(built);
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.NotEmpty(cache.PredictedSegments);
            Assert.All(cache.PredictedSegments, s => Assert.True(s.isPredicted));
            // Descending from apoapsis: the first periapsis is half a period later.
            double halfPeriod = Math.PI * Math.Sqrt(sma * sma * sma / KerbinGravParameter);
            Assert.InRange(cache.TerminalUT, halfPeriod - 1.0, halfPeriod + 1.0);
            Assert.Equal(cache.TerminalUT, cache.PredictedSegments[cache.PredictedSegments.Count - 1].endUT, 6);
            Assert.Equal("Kerbin", cache.TerminalBodyName);
            Assert.Contains(logLines, l => l.Contains("[FinalizerCache]")
                && l.Contains("On-rails orbit below floor")
                && l.Contains("rec=rec-onrails-graze"));
            Assert.Contains(logLines, l => l.Contains("[FinalizerCache]")
                && l.Contains("On-rails tail extrapolated")
                && l.Contains("terminal=Destroyed"));
        }

        [Fact]
        public void OnRailsSegment_SubSurfacePeriapsis_CachesDestroyedAtTheGround()
        {
            const double pe = -200000.0;
            const double ap = 90000.0;
            double sma = KerbinRadius + 0.5 * (pe + ap);
            double ecc = (ap - pe) / (2.0 * KerbinRadius + pe + ap);
            OrbitSegment segment = MakeSegment("Kerbin", sma, ecc, meanAnomalyAtEpoch: Math.PI, epoch: 0.0,
                startUT: 0.0, endUT: 0.0);

            bool built = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-onrails-fall", 78u, segment, 0.0, FinalizationCacheOwner.BackgroundOnRails, "unit",
                out RecordingFinalizationCache cache,
                new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() });

            Assert.True(built);
            Assert.Equal(TerminalState.Destroyed, cache.TerminalState);
            Assert.NotEmpty(cache.PredictedSegments);
            OrbitSegment last = cache.PredictedSegments[cache.PredictedSegments.Count - 1];
            Assert.True(BallisticExtrapolator.TryPropagate(last, KerbinGravParameter, cache.TerminalUT, out Vector3d pos, out _));
            Assert.InRange(Altitude(pos, KerbinRadius), -1.0, 100.0);
        }

        [Fact]
        public void OnRailsSegment_Escaping_IsNotCachedAsOrbiting()
        {
            // Hyperbolic, periapsis 100 km, already outbound; no parent body in the set.
            const double sma = -3500000.0;
            const double ecc = 1.2;
            OrbitSegment segment = MakeSegment("Kerbin", sma, ecc, meanAnomalyAtEpoch: 0.5, epoch: 0.0,
                startUT: 0.0, endUT: 0.0);

            bool built = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-onrails-escape", 79u, segment, 0.0, FinalizationCacheOwner.BackgroundOnRails, "unit",
                out RecordingFinalizationCache cache,
                new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() });

            Assert.True(built);
            Assert.NotEqual(TerminalState.Orbiting, cache.TerminalState);
            Assert.NotEmpty(cache.PredictedSegments);
        }

        [Fact]
        public void OnRailsSegment_PeriapsisInsideAtmosphereAboveDeletionAltitude_StaysOrbiting()
        {
            const double pe = 40000.0;
            const double ap = 100000.0;
            double sma = KerbinRadius + 0.5 * (pe + ap);
            double ecc = (ap - pe) / (2.0 * KerbinRadius + pe + ap);
            OrbitSegment segment = MakeSegment("Kerbin", sma, ecc, meanAnomalyAtEpoch: Math.PI, epoch: 0.0,
                startUT: 0.0, endUT: 0.0);

            bool built = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-onrails-upper-atmo", 80u, segment, 5.0, FinalizationCacheOwner.BackgroundOnRails, "unit",
                out RecordingFinalizationCache cache,
                new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() });

            Assert.True(built);
            Assert.Equal(TerminalState.Orbiting, cache.TerminalState);
            Assert.Equal(5.0, cache.TerminalUT);
            Assert.Empty(cache.PredictedSegments);
        }

        // ---- fixtures ----

        private static ExtrapolationLimits ShortLimits()
        {
            return new ExtrapolationLimits
            {
                maxHorizonYears = 0.01,
                maxSoiTransitions = 4,
                soiSampleStep = 60.0
            };
        }

        private static ExtrapolationBody MakeKerbin()
        {
            return new ExtrapolationBody
            {
                Name = "Kerbin",
                GravitationalParameter = KerbinGravParameter,
                Radius = KerbinRadius,
                AtmosphereDepth = KerbinAtmosphereDepth,
                DeletionAltitude = DeletionAltitude,
                // Flat sea-level terrain, so the surface scan reports no PQS failure.
                TerrainAltitude = (double latitude, double longitude, out double altitude) =>
                {
                    altitude = 0.0;
                    return true;
                },
            };
        }

        private static ExtrapolationBody MakeMun()
        {
            return new ExtrapolationBody
            {
                Name = "Mun",
                GravitationalParameter = MunGravParameter,
                Radius = MunRadius,
                MaxTerrainAltitude = MunMaxTerrain,
            };
        }

        private static BallisticStateVector MakeTangentialState(double radius, double speed)
        {
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(radius, 0.0, 0.0),
                velocity = new Vector3d(0.0, speed, 0.0)
            };
        }

        // A state on the periapsisAltitude x apoapsisAltitude conic at `altitude`, heading
        // down toward periapsis (perifocal frame, z = orbit normal).
        private static BallisticStateVector MakeDescendingStateOnArc(
            double periapsisAltitude,
            double apoapsisAltitude,
            double altitude)
        {
            double rp = KerbinRadius + periapsisAltitude;
            double ra = KerbinRadius + apoapsisAltitude;
            double a = 0.5 * (rp + ra);
            double e = (ra - rp) / (ra + rp);
            double p = a * (1.0 - e * e);
            double r = KerbinRadius + altitude;
            double cosNu = (p / r - 1.0) / e;
            double nu = 2.0 * Math.PI - Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosNu)));
            double scale = Math.Sqrt(KerbinGravParameter / p);
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(r * Math.Cos(nu), r * Math.Sin(nu), 0.0),
                velocity = new Vector3d(-scale * Math.Sin(nu), scale * (e + Math.Cos(nu)), 0.0)
            };
        }

        private static OrbitSegment MakeSegment(
            string bodyName,
            double sma,
            double ecc,
            double meanAnomalyAtEpoch,
            double epoch,
            double startUT,
            double endUT)
        {
            return new OrbitSegment
            {
                bodyName = bodyName,
                startUT = startUT,
                endUT = endUT,
                semiMajorAxis = sma,
                eccentricity = ecc,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                isPredicted = true
            };
        }

        private static double Altitude(Vector3d position, double radius)
        {
            return Math.Sqrt(position.x * position.x + position.y * position.y + position.z * position.z) - radius;
        }
    }
}
