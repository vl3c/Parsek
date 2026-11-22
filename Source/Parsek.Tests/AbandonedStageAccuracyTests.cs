using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Accuracy of the predicted tail an abandoned vessel gets (2026-11-18 review findings
    /// B2, B9, B10, B11, B12, L1, L3, L6, L7 and the SUB_ORBITAL / deletion-altitude
    /// leftovers). Owner principle: a ghost never just disappears; it follows its real path to
    /// a natural ending. Plain Kepler arcs, no drag.
    /// </summary>
    [Collection("Sequential")]
    public class AbandonedStageAccuracyTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinGravParameter = 3.5316e12;
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double MunRadius = 200000.0;
        private const double MunGravParameter = 6.5138398e10;
        private const double MunSoi = 2429559.1;
        private const double MunOrbitRadius = 12000000.0;

        private readonly List<string> logLines = new List<string>();

        public AbandonedStageAccuracyTests()
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

        private static ExtrapolationBody MakeKerbin()
        {
            return new ExtrapolationBody
            {
                Name = "Kerbin",
                GravitationalParameter = KerbinGravParameter,
                Radius = KerbinRadius,
                AtmosphereDepth = KerbinAtmosphereDepth,
                DeletionAltitude = OrbitClearanceTestValues.KerbinDeletionAltitude,
                SphereOfInfluence = 84159286.0
            };
        }

        private static PatchedConicSnapshotResult NullSolverSnapshot()
        {
            return new PatchedConicSnapshotResult
            {
                FailureReason = PatchedConicSnapshotFailureReason.NullSolver,
                Segments = new List<OrbitSegment>()
            };
        }

        // ---------------------------------------------------------------- B2

        /// <summary>
        /// A NullSolver snapshot is every vessel the player is not flying (only the active
        /// vessel carries a patched-conic solver). When the vessel is alive its live orbit is
        /// its real coast: a sub-orbital stage left at 30 km going up falls to the ground at the
        /// end of its arc instead of being classified Destroyed on the spot.
        /// </summary>
        [Fact]
        public void NullSolver_LiveVesselConfirmedAlive_FollowsItsLiveOrbitToImpact()
        {
            var rec = new Recording
            {
                RecordingId = "asr-acc-live-coast",
                ExplicitStartUT = 400.0,
                ExplicitEndUT = 500.0
            };
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };

            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                NullSolverSnapshot(),
                bodies,
                delegate(out BallisticStateVector startState)
                {
                    startState = new BallisticStateVector
                    {
                        ut = 500.0,
                        bodyName = "Kerbin",
                        position = new Vector3d(KerbinRadius + 30000.0, 0.0, 0.0),
                        velocity = new Vector3d(400.0, 900.0, 0.0),
                        orbitalFrameRotation = Quaternion.identity
                    };
                    return true;
                },
                (startState, extrapolationBodies) => BallisticExtrapolator.Extrapolate(
                    startState, extrapolationBodies, warnOnSubSurfaceStart: false),
                null,
                true,
                Vector3.zero,
                out IncompleteBallisticFinalizationResult result);

            Assert.True(built);
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.NotEqual(ExtrapolationFailureReason.NoSolverStart, result.extrapolationFailureReason);
            Assert.True(result.terminalUT > 600.0, "terminalUT=" + result.terminalUT);
            Assert.NotNull(result.appendedOrbitSegments);
            Assert.NotEmpty(result.appendedOrbitSegments);
            Assert.Contains(logLines, l => l.Contains("solver-less live vessel 'asr-acc-live-coast' follows its live orbit"));
            Assert.DoesNotContain(logLines, l => l.Contains("no-solver live-orbit fallback"));
        }

        [Fact]
        public void NullSolver_LiveVesselUnderOpenCanopy_DeclinesTheBallisticTail()
        {
            var rec = new Recording
            {
                RecordingId = "asr-acc-live-chute",
                ExplicitStartUT = 400.0,
                ExplicitEndUT = 500.0
            };
            rec.PartEvents.Add(new PartEvent
            {
                ut = 450.0,
                partPersistentId = 4242u,
                eventType = PartEventType.ParachuteDeployed
            });
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };

            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                NullSolverSnapshot(),
                bodies,
                delegate(out BallisticStateVector startState)
                {
                    startState = new BallisticStateVector
                    {
                        ut = 500.0,
                        bodyName = "Kerbin",
                        position = new Vector3d(KerbinRadius + 2000.0, 0.0, 0.0),
                        velocity = new Vector3d(-8.0, 175.0, 0.0),
                        orbitalFrameRotation = Quaternion.identity
                    };
                    return true;
                },
                (startState, extrapolationBodies) => BallisticExtrapolator.Extrapolate(
                    startState, extrapolationBodies, warnOnSubSurfaceStart: false),
                null,
                true,
                Vector3.zero,
                out IncompleteBallisticFinalizationResult result);

            Assert.False(built);
            Assert.Contains(logLines, l => l.Contains("solver-less live vessel 'asr-acc-live-chute' has an open parachute (pid=4242)"));
        }

        [Fact]
        public void IsNoSolverDestroyedFallback_ConfirmedAliveVessel_IsNotDestroyed()
        {
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsNoSolverDestroyedFallback(
                PatchedConicSnapshotFailureReason.NullSolver, true, ExtrapolationFailureReason.None));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsNoSolverDestroyedFallback(
                PatchedConicSnapshotFailureReason.NullSolver, true, ExtrapolationFailureReason.None,
                liveVesselConfirmedAlive: true));
        }

        // ---------------------------------------------------------------- B9

        [Fact]
        public void HistoricalRotationShift_OneMinuteOnKerbin_IsAboutOneDegreeWest()
        {
            const double kerbinRotationPeriod = 21549.425;
            double shift = IncompleteBallisticSceneExitFinalizer.ComputeHistoricalRotationLanShiftDegrees(
                sampleUT: 1000.0, currentUT: 1060.0, rotationPeriod: kerbinRotationPeriod, bodyRotates: true);
            Assert.Equal(-60.0 * 360.0 / kerbinRotationPeriod, shift, 9);
            Assert.InRange(shift, -1.01, -0.99);

            Assert.Equal(0.0, IncompleteBallisticSceneExitFinalizer.ComputeHistoricalRotationLanShiftDegrees(
                1000.0, 1060.0, kerbinRotationPeriod, bodyRotates: false));
            Assert.Equal(0.0, IncompleteBallisticSceneExitFinalizer.ComputeHistoricalRotationLanShiftDegrees(
                1000.0, 1060.0, double.NaN, bodyRotates: true));
            Assert.Equal(0.0, IncompleteBallisticSceneExitFinalizer.ComputeHistoricalRotationLanShiftDegrees(
                1060.0, 1060.0, kerbinRotationPeriod, bodyRotates: true));
        }

        /// <summary>
        /// The shift is a rotation about the spin axis: every position of the conic moves by
        /// the shift in longitude and keeps its latitude and radius. Combined with the
        /// body-fixed convention (body-fixed lon = inertial lon - 360 * (ut - ref) / P), a sample
        /// rebuilt in the body's current orientation and shifted by
        /// <see cref="IncompleteBallisticSceneExitFinalizer.ComputeHistoricalRotationLanShiftDegrees"/>
        /// reads back its own recorded longitude at its own UT.
        /// </summary>
        [Fact]
        public void HistoricalRotationShift_RotatesTheConicAboutThePole()
        {
            var segment = new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 1000.0,
                endUT = 1500.0,
                inclination = 28.0,
                eccentricity = 0.3,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 0.4,
                argumentOfPeriapsis = 40.0,
                meanAnomalyAtEpoch = 0.7,
                epoch = 1000.0,
                isPredicted = true
            };
            const double shift = -1.25;
            OrbitSegment shifted = segment;
            shifted.longitudeOfAscendingNode =
                IncompleteBallisticSceneExitFinalizer.ShiftLongitudeOfAscendingNodeDegrees(segment.longitudeOfAscendingNode, shift);
            Assert.InRange(shifted.longitudeOfAscendingNode, 359.0, 360.0);

            foreach (double ut in new[] { 1000.0, 1200.0, 1450.0 })
            {
                Assert.True(BallisticExtrapolator.TryPropagate(segment, KerbinGravParameter, ut, out Vector3d a, out _));
                Assert.True(BallisticExtrapolator.TryPropagate(shifted, KerbinGravParameter, ut, out Vector3d b, out _));
                BallisticExtrapolator.GetApproximateLatitudeLongitude(a, out double latA, out double lonA);
                BallisticExtrapolator.GetApproximateLatitudeLongitude(b, out double latB, out double lonB);
                Assert.Equal(latA, latB, 6);
                Assert.Equal(a.magnitude, b.magnitude, 3);
                double dLon = lonB - lonA;
                if (dLon > 180.0) dLon -= 360.0;
                if (dLon < -180.0) dLon += 360.0;
                Assert.Equal(shift, dLon, 6);
            }

            // Round trip through the body-fixed convention: a sample 60 s old rebuilt "now"
            // sits at inertial lon = recorded lon; shifted, then read at its own UT with the
            // reference at now, it is back on the recorded longitude.
            const double period = 21549.425;
            const double recordedLon = 12.0;
            double age = 60.0;
            double lanShift = IncompleteBallisticSceneExitFinalizer.ComputeHistoricalRotationLanShiftDegrees(
                1000.0, 1000.0 + age, period, true);
            double inertialLonAtSample = recordedLon + lanShift;
            double bodyFixedReadAtSample = inertialLonAtSample - ((1000.0 - (1000.0 + age)) * 360.0) / period;
            Assert.Equal(recordedLon, bodyFixedReadAtSample, 9);
        }

        // ---------------------------------------------------------------- B10 / L1

        private static Dictionary<string, ExtrapolationBody> MakeKerbinWithMun(double munPhaseAtZero)
        {
            double munRate = Math.Sqrt(KerbinGravParameter / (MunOrbitRadius * MunOrbitRadius * MunOrbitRadius));
            var kerbin = new ExtrapolationBody
            {
                Name = "Kerbin",
                GravitationalParameter = KerbinGravParameter,
                Radius = KerbinRadius,
                SphereOfInfluence = 84159286.0
            };
            var mun = new ExtrapolationBody
            {
                Name = "Mun",
                ParentBodyName = "Kerbin",
                GravitationalParameter = MunGravParameter,
                Radius = MunRadius,
                SphereOfInfluence = MunSoi,
                ParentFrameState = (double ut, out Vector3d position, out Vector3d velocity) =>
                {
                    double angle = munPhaseAtZero + munRate * ut;
                    position = new Vector3d(MunOrbitRadius * Math.Cos(angle), MunOrbitRadius * Math.Sin(angle), 0.0);
                    velocity = new Vector3d(
                        -MunOrbitRadius * munRate * Math.Sin(angle),
                        MunOrbitRadius * munRate * Math.Cos(angle),
                        0.0);
                }
            };
            return new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = kerbin, ["Mun"] = mun };
        }

        /// <summary>
        /// A coast from low Kerbin orbit whose apoapsis (9 700 km) stops 2 300 km short of the
        /// Mun's orbit, timed so the Mun passes overhead at apoapsis: the closest approach is
        /// 0.95 of the Mun's sphere of influence, inside it for well under an hour. The old fixed
        /// one-hour sampling never put a sample inside; the radial-band search refines the
        /// closest approach and enters the Mun's sphere, exactly once (no bounce back out at the
        /// entry instant).
        /// </summary>
        [Fact]
        public void Extrapolate_GrazingMunPass_EntersTheMunSphereOnce()
        {
            const double peRadius = 700000.0;
            const double apRadius = 9700000.0;
            double sma = 0.5 * (peRadius + apRadius);
            double period = 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / KerbinGravParameter);
            double apoapsisUT = 0.5 * period;
            double munRate = Math.Sqrt(KerbinGravParameter / (MunOrbitRadius * MunOrbitRadius * MunOrbitRadius));
            double munPhase = Math.PI - munRate * apoapsisUT;
            var bodies = MakeKerbinWithMun(munPhase);
            double pePeed = Math.Sqrt(KerbinGravParameter * (2.0 / peRadius - 1.0 / sma));

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Kerbin",
                    position = new Vector3d(peRadius, 0.0, 0.0),
                    velocity = new Vector3d(0.0, pePeed, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 300000.0 / BallisticExtrapolator.SecondsPerYear,
                    maxSoiTransitions = 8,
                    soiSampleStep = 3600.0
                });

            List<string> entries = logLines
                .Where(l => l.Contains("SOI transition:") && l.Contains("kind=ChildEntry") && l.Contains("child=Mun"))
                .ToList();
            Assert.Single(entries);
            Assert.Contains(result.segments, s => s.bodyName == "Mun");
            OrbitSegment munSegment = result.segments.First(s => s.bodyName == "Mun");
            Assert.InRange(munSegment.startUT, apoapsisUT - 3000.0, apoapsisUT);
            // Out again well after the entry, not at the entry instant.
            Assert.True(munSegment.endUT > munSegment.startUT + 300.0,
                "munSegment=[" + munSegment.startUT + "," + munSegment.endUT + "]");
            Assert.DoesNotContain(logLines, l => l.Contains("soi-transition-cap"));
        }

        [Fact]
        public void Extrapolate_ApoapsisShortOfTheMunBand_NoEntry()
        {
            const double peRadius = 700000.0;
            const double apRadius = 9000000.0;
            double sma = 0.5 * (peRadius + apRadius);
            var bodies = MakeKerbinWithMun(0.0);
            double pePeed = Math.Sqrt(KerbinGravParameter * (2.0 / peRadius - 1.0 / sma));

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Kerbin",
                    position = new Vector3d(peRadius, 0.0, 0.0),
                    velocity = new Vector3d(0.0, pePeed, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 300000.0 / BallisticExtrapolator.SecondsPerYear,
                    maxSoiTransitions = 8,
                    soiSampleStep = 3600.0
                });

            Assert.All(result.segments, s => Assert.Equal("Kerbin", s.bodyName));
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
        }

        [Fact]
        public void RadialBandWindows_CoverExactlyTheBand()
        {
            var windows = new List<KeyValuePair<double, double>>();
            const double a = 5200000.0;
            const double e = 0.8653846153846154; // Pe 700 km, Ap 9 700 km
            Assert.True(BallisticExtrapolator.TryComputeRadialBandMeanAnomalyWindows(
                a, e, true, 700000.0, 9700000.0, 9570440.9, 14429559.1, windows));
            Assert.Single(windows);
            Assert.True(windows[0].Key > 0.0 && windows[0].Value < 2.0 * Math.PI);
            Assert.True(windows[0].Key < Math.PI && windows[0].Value > Math.PI);

            // Entirely below the band: nothing to search.
            Assert.True(BallisticExtrapolator.TryComputeRadialBandMeanAnomalyWindows(
                a, e, true, 700000.0, 9000000.0, 9570440.9, 14429559.1, windows));
            Assert.Empty(windows);

            // Band in the middle of the radius range: one window going out, one coming back.
            Assert.True(BallisticExtrapolator.TryComputeRadialBandMeanAnomalyWindows(
                a, e, true, 700000.0, 9700000.0, 3000000.0, 4000000.0, windows));
            Assert.Equal(2, windows.Count);
            Assert.Equal(2.0 * Math.PI, windows[0].Key + windows[1].Value, 9);

            // Hyperbolic escape crossing the band twice (in and out) around periapsis.
            Assert.True(BallisticExtrapolator.TryComputeRadialBandMeanAnomalyWindows(
                -2000000.0, 1.5, false, 1000000.0, double.PositiveInfinity, 3000000.0, 4000000.0, windows));
            Assert.Equal(2, windows.Count);
            Assert.True(windows[0].Value < 0.0 && windows[1].Key > 0.0);
        }

        // ---------------------------------------------------------------- B11

        /// <summary>
        /// An airless orbit whose low point (3 km) sits among the peaks (5 km): the first pass
        /// crosses flat ground, but the body turns under the conic and a later pass meets the
        /// ridge. It ends at that impact instead of coasting to the horizon.
        /// </summary>
        [Fact]
        public void Extrapolate_AirlessLowPointAmongThePeaks_LaterPassHitsTheRidge()
        {
            const double rotationPeriod = 138984.0;
            const double peakAltitude = 5000.0;
            const double peRadius = MunRadius + 3000.0;
            const double apRadius = MunRadius + 50000.0;
            double sma = 0.5 * (peRadius + apRadius);
            double period = 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / MunGravParameter);
            double peSpeed = Math.Sqrt(MunGravParameter * (2.0 / peRadius - 1.0 / sma));
            var mun = new ExtrapolationBody
            {
                Name = "Mun",
                GravitationalParameter = MunGravParameter,
                Radius = MunRadius,
                MaxTerrainAltitude = peakAltitude,
                SphereOfInfluence = MunSoi,
                TerrainAltitude = (double lat, double lon, out double altitude) =>
                {
                    altitude = lon >= -75.0 && lon <= -55.0 ? peakAltitude : 0.0;
                    return true;
                },
                SurfaceCoordinates = (double ut, Vector3d position, out double lat, out double lon) =>
                {
                    BallisticExtrapolator.GetApproximateLatitudeLongitude(position, out lat, out lon);
                    lon -= ut * 360.0 / rotationPeriod;
                    lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
                }
            };
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Mun"] = mun };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Mun",
                    position = new Vector3d(peRadius, 0.0, 0.0),
                    velocity = new Vector3d(0.0, peSpeed, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                bodies,
                new ExtrapolationLimits
                {
                    maxHorizonYears = 100.0 * period / BallisticExtrapolator.SecondsPerYear,
                    maxSoiTransitions = 4,
                    soiSampleStep = 600.0
                });

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.True(result.terminalUT > 2.0 * period, "terminalUT=" + result.terminalUT + " period=" + period);
            Assert.Contains(logLines, l => l.Contains("Later-pass terrain impact: body=Mun"));
            Assert.Contains(logLines, l => l.Contains("Terminal reason=surface-impact"));
        }

        // ---------------------------------------------------------------- L3

        [Fact]
        public void StampPredictedSegmentSpin_StampsPredictedSegmentsWithoutSpin()
        {
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment { startUT = 0, endUT = 10, isPredicted = true },
                new OrbitSegment { startUT = 10, endUT = 20, isPredicted = true, angularVelocity = new Vector3(0f, 0f, 2f) },
                new OrbitSegment { startUT = 20, endUT = 30, isPredicted = false }
            };
            var spin = new Vector3(0.3f, -0.1f, 0.2f);

            int stamped = IncompleteBallisticSceneExitFinalizer.StampPredictedSegmentSpin("asr-acc-spin", segments, spin);

            Assert.Equal(1, stamped);
            Assert.Equal(spin, segments[0].angularVelocity);
            Assert.Equal(new Vector3(0f, 0f, 2f), segments[1].angularVelocity);
            Assert.Equal(Vector3.zero, segments[2].angularVelocity);
            Assert.True(TrajectoryMath.IsSpinning(segments[0]));

            // At or under the threshold nothing is stamped.
            var calm = new List<OrbitSegment> { new OrbitSegment { isPredicted = true } };
            Assert.Equal(0, IncompleteBallisticSceneExitFinalizer.StampPredictedSegmentSpin(
                "asr-acc-spin", calm, new Vector3(0.01f, 0f, 0f)));
            Assert.Equal(Vector3.zero, calm[0].angularVelocity);
        }

        [Fact]
        public void TumblingLiveVessel_TailSegmentsCarryItsSpin()
        {
            var rec = new Recording
            {
                RecordingId = "asr-acc-tumble",
                ExplicitStartUT = 400.0,
                ExplicitEndUT = 500.0
            };
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() };
            var spin = new Vector3(0f, 0.8f, 0f);

            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                NullSolverSnapshot(),
                bodies,
                delegate(out BallisticStateVector startState)
                {
                    startState = new BallisticStateVector
                    {
                        ut = 500.0,
                        bodyName = "Kerbin",
                        position = new Vector3d(KerbinRadius + 30000.0, 0.0, 0.0),
                        velocity = new Vector3d(400.0, 900.0, 0.0),
                        orbitalFrameRotation = Quaternion.identity
                    };
                    return true;
                },
                (startState, extrapolationBodies) => BallisticExtrapolator.Extrapolate(
                    startState, extrapolationBodies, warnOnSubSurfaceStart: false),
                null,
                true,
                spin,
                out IncompleteBallisticFinalizationResult result);

            Assert.True(built);
            Assert.NotEmpty(result.appendedOrbitSegments);
            Assert.All(result.appendedOrbitSegments, s => Assert.Equal(spin, s.angularVelocity));
        }

        // ---------------------------------------------------------------- L6

        [Fact]
        public void HorizonYear_IsTheCalendarYearTheCallerPasses_ElseTheHomeBodyYear()
        {
            Assert.Equal(426.0 * 21600.0, BallisticExtrapolator.ResolveHorizonSecondsPerYear(ExtrapolationLimits.Default));
            var earth = ExtrapolationLimits.Default;
            earth.calendarSecondsPerYear = 365.0 * 86400.0;
            Assert.Equal(365.0 * 86400.0, BallisticExtrapolator.ResolveHorizonSecondsPerYear(earth));

            double circular = Math.Sqrt(KerbinGravParameter / (KerbinRadius + 100000.0));
            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Kerbin",
                    position = new Vector3d(KerbinRadius + 100000.0, 0.0, 0.0),
                    velocity = new Vector3d(0.0, circular, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                new Dictionary<string, ExtrapolationBody> { ["Kerbin"] = MakeKerbin() },
                new ExtrapolationLimits { maxHorizonYears = 0.001, maxSoiTransitions = 4, soiSampleStep = 60.0 },
                stopOnFinalConic: false);
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal(0.001 * 426.0 * 21600.0, result.terminalUT, 3);
        }

        // ---------------------------------------------------------------- L7

        [Fact]
        public void ResolveSurfaceAltitude_OceanClampsToTheSea_OceanlessKeepsTheGround()
        {
            var ocean = new ExtrapolationBody { Name = "Kerbin" };
            var dry = new ExtrapolationBody { Name = "Moho", TerrainBelowDatum = true, MinTerrainAltitude = -3000.0 };
            Assert.Equal(0.0, BallisticExtrapolator.ResolveSurfaceAltitude(ocean, -1500.0));
            Assert.Equal(-1500.0, BallisticExtrapolator.ResolveSurfaceAltitude(dry, -1500.0));
            Assert.Equal(800.0, BallisticExtrapolator.ResolveSurfaceAltitude(dry, 800.0));
            Assert.Equal(0.0, ocean.SurfaceFloorAltitude);
            Assert.Equal(-3000.0, dry.SurfaceFloorAltitude);
        }

        private static ExtrapolationBody MakeDryBasin(bool terrainBelowDatum)
        {
            return new ExtrapolationBody
            {
                Name = "Basin",
                GravitationalParameter = MunGravParameter,
                Radius = MunRadius,
                MaxTerrainAltitude = 0.0,
                TerrainBelowDatum = terrainBelowDatum,
                MinTerrainAltitude = -3000.0,
                SphereOfInfluence = MunSoi,
                TerrainAltitude = (double lat, double lon, out double altitude) =>
                {
                    altitude = -2000.0;
                    return true;
                }
            };
        }

        [Fact]
        public void Extrapolate_FallIntoABasinBelowTheDatum_EndsAtTheBasinFloor()
        {
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Basin"] = MakeDryBasin(true) };
            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Basin",
                    position = new Vector3d(MunRadius + 5000.0, 0.0, 0.0),
                    velocity = new Vector3d(-50.0, 100.0, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                bodies);

            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            double terminalAltitude = result.terminalPosition.magnitude - MunRadius;
            Assert.InRange(terminalAltitude, -2010.0, -1990.0);

            // The same body with an ocean: the surface below the datum is the sea.
            logLines.Clear();
            var wet = new Dictionary<string, ExtrapolationBody> { ["Basin"] = MakeDryBasin(false) };
            ExtrapolationResult wetResult = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Basin",
                    position = new Vector3d(MunRadius + 5000.0, 0.0, 0.0),
                    velocity = new Vector3d(-50.0, 100.0, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                wet);
            Assert.Equal(TerminalState.Destroyed, wetResult.terminalState);
            Assert.InRange(wetResult.terminalPosition.magnitude - MunRadius, -10.0, 10.0);
        }

        [Fact]
        public void Extrapolate_StartInsideABasinBelowTheDatum_IsNotASubSurfaceStart()
        {
            var bodies = new Dictionary<string, ExtrapolationBody> { ["Basin"] = MakeDryBasin(true) };
            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                new BallisticStateVector
                {
                    ut = 0.0,
                    bodyName = "Basin",
                    position = new Vector3d(MunRadius - 1500.0, 0.0, 0.0),
                    velocity = new Vector3d(20.0, 80.0, 0.0),
                    orbitalFrameRotation = Quaternion.identity
                },
                bodies);

            Assert.NotEqual(ExtrapolationFailureReason.SubSurfaceStart, result.failureReason);
            Assert.Equal(TerminalState.Destroyed, result.terminalState);
            Assert.True(result.terminalUT > 0.0);
        }

        // ---------------------------------------------------------------- leftovers

        [Fact]
        public void ShouldExtrapolate_SubOrbitalBoundClearOfTheFloor_IsAStableOrbit()
        {
            double floor = OrbitClearanceTestValues.KerbinDeletionAltitude;
            // Periapsis between the deletion altitude and the atmosphere top: stock keeps it.
            Assert.False(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.SUB_ORBITAL, 0.05, 40000.0, floor));
            // Under the floor, unbound, or unknown: still a fall to follow.
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.SUB_ORBITAL, 0.05, floor - 1.0, floor));
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.SUB_ORBITAL, 1.2, 40000.0, floor));
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.SUB_ORBITAL, 0.05, double.NaN, floor));
            // And the commit classification reads that orbit as Orbiting.
            Assert.Equal(TerminalState.Orbiting, RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.SUB_ORBITAL, 0.05, KerbinRadius + 40000.0, KerbinRadius, floor));
        }

        [Fact]
        public void ShouldExtrapolate_FlyingBoundClearOfTheFloor_IsAStableOrbit()
        {
            double floor = OrbitClearanceTestValues.KerbinDeletionAltitude;
            // A FLYING coast whose low point clears the floor is the orbit stock keeps.
            Assert.False(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.FLYING, 0.05, 40000.0, floor));
            // Under the floor (every vessel flying in the low air), unbound, or unknown:
            // still followed.
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.FLYING, 0.05, floor - 1.0, floor));
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.FLYING, 0.9, -500000.0, floor));
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.FLYING, 1.2, 40000.0, floor));
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.FLYING, 0.05, double.NaN, floor));
            Assert.True(BallisticExtrapolator.ShouldExtrapolate(
                Vessel.Situations.FLYING, 0.05, double.PositiveInfinity, floor));
        }
    }
}
