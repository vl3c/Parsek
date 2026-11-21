using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q8 (2026-11-21): a predicted tail with no natural end is followed to its
    /// final conic around the last body it reaches and ends there (Orbiting on a stable
    /// orbit, which spawns with crew aboard), instead of running to the horizon.
    /// </summary>
    [Collection("Sequential")]
    public class FinalConicEndingTests : IDisposable
    {
        private const double SunGravParameter = 1.1723328e18;
        private const double SunRadius = 261600000.0;
        private const double KerbinGravParameter = 3.5316e12;
        private const double KerbinRadius = 600000.0;
        private const double KerbinAtmosphereDepth = 70000.0;
        private const double KerbinSoi = 84159286.0;
        private const double KerbinOrbitRadius = 13599840256.0;
        private const double MunGravParameter = 6.5138398e10;
        private const double MunRadius = 200000.0;
        private const double MunSoi = 2429559.1;
        private const double MunOrbitRadius = 12000000.0;

        private readonly List<string> logLines = new List<string>();

        public FinalConicEndingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ bodies

        /// <summary>A body on a circular orbit of its parent, in the parent's frame.</summary>
        private static ParentFrameStateResolver CircularParentState(double orbitRadius, double parentGravParameter)
        {
            double meanMotion = Math.Sqrt(parentGravParameter / (orbitRadius * orbitRadius * orbitRadius));
            return (double ut, out Vector3d position, out Vector3d velocity) =>
            {
                double angle = meanMotion * ut;
                position = new Vector3d(orbitRadius * Math.Cos(angle), orbitRadius * Math.Sin(angle), 0.0);
                velocity = new Vector3d(
                    -orbitRadius * meanMotion * Math.Sin(angle),
                    orbitRadius * meanMotion * Math.Cos(angle),
                    0.0);
            };
        }

        private static Dictionary<string, ExtrapolationBody> SunAndKerbin()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Sun"] = new ExtrapolationBody
                {
                    Name = "Sun",
                    GravitationalParameter = SunGravParameter,
                    Radius = SunRadius,
                },
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    ParentBodyName = "Sun",
                    GravitationalParameter = KerbinGravParameter,
                    Radius = KerbinRadius,
                    AtmosphereDepth = KerbinAtmosphereDepth,
                    SphereOfInfluence = KerbinSoi,
                    ParentFrameState = CircularParentState(KerbinOrbitRadius, SunGravParameter),
                },
            };
        }

        private static Dictionary<string, ExtrapolationBody> KerbinAndMun()
        {
            return new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = new ExtrapolationBody
                {
                    Name = "Kerbin",
                    GravitationalParameter = KerbinGravParameter,
                    Radius = KerbinRadius,
                    AtmosphereDepth = KerbinAtmosphereDepth,
                },
                ["Mun"] = new ExtrapolationBody
                {
                    Name = "Mun",
                    ParentBodyName = "Kerbin",
                    GravitationalParameter = MunGravParameter,
                    Radius = MunRadius,
                    SphereOfInfluence = MunSoi,
                    ParentFrameState = CircularParentState(MunOrbitRadius, KerbinGravParameter),
                },
            };
        }

        /// <summary>A capsule leaving Kerbin from a 100 km periapsis at 3500 m/s.</summary>
        private static BallisticStateVector KerbinEscapeStart()
        {
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(KerbinRadius + 100000.0, 0.0, 0.0),
                velocity = new Vector3d(0.0, 3500.0, 0.0),
            };
        }

        private static double Magnitude(Vector3d v) => Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        // ------------------------------------------------------------------ extrapolator

        [Fact]
        public void KerbinEscape_EndsOrbitingTheSunAtTheSoiEntry_WithTheSolarElements()
        {
            Dictionary<string, ExtrapolationBody> bodies = SunAndKerbin();

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(KerbinEscapeStart(), bodies);

            // PqsUnavailable is the soft no-terrain fallback of a body built without a resolver.
            Assert.True(result.failureReason == ExtrapolationFailureReason.None
                || result.failureReason == ExtrapolationFailureReason.PqsUnavailable,
                result.failureReason.ToString());
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal("Sun", result.terminalBodyName);
            Assert.True(result.endedOnFinalConic);
            Assert.Equal(2, result.segments.Count);

            OrbitSegment kerbinLeg = result.segments[0];
            OrbitSegment sunLeg = result.segments[1];
            Assert.Equal("Kerbin", kerbinLeg.bodyName);
            Assert.True(kerbinLeg.eccentricity > 1.0);
            Assert.Equal("Sun", sunLeg.bodyName);
            Assert.True(sunLeg.eccentricity < 1.0 && sunLeg.semiMajorAxis > 0.0);

            // Entered at the Kerbin sphere exit, ended one settle span later: hours, not years.
            Assert.Equal(kerbinLeg.endUT, result.finalConicStartUT, 6);
            Assert.Equal(kerbinLeg.endUT, sunLeg.startUT, 6);
            Assert.Equal(sunLeg.startUT + BallisticExtrapolator.FinalConicSettleSeconds, sunLeg.endUT, 6);
            Assert.Equal(sunLeg.endUT, result.terminalUT, 6);
            Assert.True(result.terminalUT < 30.0 * 86400.0, "ends at the sphere exit, not at the horizon");

            // The solar elements are the conic of the boundary state.
            Assert.True(BallisticExtrapolator.TryPropagate(
                kerbinLeg, KerbinGravParameter, kerbinLeg.endUT, out Vector3d exitPos, out Vector3d exitVel));
            bodies["Kerbin"].ParentFrameState(kerbinLeg.endUT, out Vector3d kerbinPos, out Vector3d kerbinVel);
            Assert.True(BallisticExtrapolator.TryPropagate(
                sunLeg, SunGravParameter, sunLeg.startUT, out Vector3d sunPos, out Vector3d sunVel));
            Assert.True(Magnitude(exitPos + kerbinPos - sunPos) < 10.0);
            Assert.True(Magnitude(exitVel + kerbinVel - sunVel) < 0.01);

            Assert.Contains(logLines, l => l.Contains("[Extrapolator]")
                && l.Contains("Terminal reason=" + BallisticExtrapolator.TerminalReasonStableConic)
                && l.Contains("body=Sun"));
            Assert.DoesNotContain(logLines, l => l.Contains("Terminal reason=horizon-cap"));
        }

        [Fact]
        public void MunEscape_BackIntoAStableKerbinOrbit_EndsOrbitingKerbinAtTheMunExit()
        {
            // Just inside the Mun's sphere, climbing out slowly: the Kerbin conic it lands on
            // has its periapsis near the Mun's orbit, far above Kerbin.
            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Mun",
                position = new Vector3d(2400000.0, 0.0, 0.0),
                velocity = new Vector3d(100.0, 100.0, 0.0),
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, KerbinAndMun());

            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal("Kerbin", result.terminalBodyName);
            Assert.True(result.endedOnFinalConic);
            Assert.Equal(2, result.segments.Count);
            Assert.Equal("Mun", result.segments[0].bodyName);
            Assert.Equal("Kerbin", result.segments[1].bodyName);
            Assert.Equal(result.segments[0].endUT, result.finalConicStartUT, 6);
            Assert.Equal(result.segments[1].endUT, result.terminalUT, 6);
            double periapsis = result.segments[1].semiMajorAxis * (1.0 - result.segments[1].eccentricity);
            Assert.True(periapsis > KerbinRadius + KerbinAtmosphereDepth);
            Assert.Contains(logLines, l => l.Contains("Terminal reason=" + BallisticExtrapolator.TerminalReasonStableConic)
                && l.Contains("body=Kerbin"));
        }

        [Fact]
        public void StableOrbitStart_EndsAtOnce()
        {
            double circular = Math.Sqrt(KerbinGravParameter / (KerbinRadius + 100000.0));
            var start = new BallisticStateVector
            {
                ut = 500.0,
                bodyName = "Kerbin",
                position = new Vector3d(KerbinRadius + 100000.0, 0.0, 0.0),
                velocity = new Vector3d(0.0, circular, 0.0),
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, KerbinAndMun());

            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.True(result.endedOnFinalConic);
            Assert.Equal(500.0, result.finalConicStartUT, 9);
            Assert.Single(result.segments);
            Assert.Equal(500.0 + BallisticExtrapolator.FinalConicSettleSeconds, result.terminalUT, 6);
            Assert.Contains(logLines, l => l.Contains("Terminal reason=" + BallisticExtrapolator.TerminalReasonStableConic)
                && l.Contains("enteredUT=500.000"));
        }

        [Fact]
        public void StableOrbitStart_StopDisabled_RunsToTheHorizon()
        {
            double circular = Math.Sqrt(KerbinGravParameter / (KerbinRadius + 100000.0));
            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(KerbinRadius + 100000.0, 0.0, 0.0),
                velocity = new Vector3d(0.0, circular, 0.0),
            };
            var limits = new ExtrapolationLimits { maxHorizonYears = 0.0001, maxSoiTransitions = 4, soiSampleStep = 60.0 };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(
                start, KerbinAndMun(), limits, stopOnFinalConic: false);

            Assert.False(result.endedOnFinalConic);
            Assert.Equal(limits.maxHorizonYears * BallisticExtrapolator.SecondsPerYear, result.terminalUT, 4);
            Assert.Contains(logLines, l => l.Contains("Terminal reason=horizon-cap") && l.Contains("finalConicStop=False"));
        }

        [Fact]
        public void LowPointAmongThePeaks_NoImpactFound_StillCapsAtTheHorizonWithALog()
        {
            // Airless body, flat ground at the datum but peaks reported at 7 km: a periapsis at
            // 5 km is under the floor (not stable) and never meets the ground, so only the
            // horizon ends it, and the finalizer's floor check turns it SubOrbital.
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Mun"] = new ExtrapolationBody
                {
                    Name = "Mun",
                    GravitationalParameter = MunGravParameter,
                    Radius = MunRadius,
                    MaxTerrainAltitude = 7000.0,
                    TerrainAltitude = (double lat, double lon, out double altitude) =>
                    {
                        altitude = 0.0;
                        return true;
                    },
                },
            };
            double rp = MunRadius + 5000.0;
            double ra = MunRadius + 20000.0;
            double sma = 0.5 * (rp + ra);
            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Mun",
                position = new Vector3d(ra, 0.0, 0.0),
                velocity = new Vector3d(0.0, Math.Sqrt(MunGravParameter * (2.0 / ra - 1.0 / sma)), 0.0),
            };
            var limits = new ExtrapolationLimits { maxHorizonYears = 0.002, maxSoiTransitions = 4, soiSampleStep = 60.0 };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies, limits);

            Assert.False(result.endedOnFinalConic);
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.Equal(limits.maxHorizonYears * BallisticExtrapolator.SecondsPerYear, result.terminalUT, 3);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Terminal reason=horizon-cap")
                && l.Contains("floor=7000.0")
                && l.Contains("finalConicStop=True"));
            Assert.Equal(TerminalState.SubOrbital,
                IncompleteBallisticSceneExitFinalizer.ApplyPeriapsisFloorToExtrapolatedTerminal(
                    result.terminalState, result.segments, bodies, "rec-peaks"));
        }

        [Fact]
        public void OpenConicAroundTheSun_NothingAhead_EndsSubOrbitalAtOnce()
        {
            // An open conic around a body with no parent (KSP's Sun has an infinite sphere):
            // nothing can change it, so it is final; Orbiting is impossible, the escape ending
            // is SubOrbital on its open conic (spawned on its path, owner ruling Q10).
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Sun"] = new ExtrapolationBody
                {
                    Name = "Sun",
                    GravitationalParameter = SunGravParameter,
                    Radius = SunRadius,
                },
            };
            double r = 2.0 * KerbinOrbitRadius;
            var start = new BallisticStateVector
            {
                ut = 100.0,
                bodyName = "Sun",
                position = new Vector3d(r, 0.0, 0.0),
                velocity = new Vector3d(0.0, 1.5 * Math.Sqrt(2.0 * SunGravParameter / r), 0.0),
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            Assert.Equal(TerminalState.SubOrbital, result.terminalState);
            Assert.True(result.endedOnFinalConic);
            Assert.Single(result.segments);
            Assert.True(result.segments[0].eccentricity > 1.0);
            Assert.Equal(100.0 + BallisticExtrapolator.FinalConicSettleSeconds, result.terminalUT, 6);
            Assert.Contains(logLines, l => l.Contains("Terminal reason=" + BallisticExtrapolator.TerminalReasonFinalEscape)
                && l.Contains("body=Sun"));
        }

        [Theory]
        [InlineData(1.0e6, 60.0)]
        [InlineData(100.0, 25.0)]
        [InlineData(double.NaN, 60.0)]
        [InlineData(double.PositiveInfinity, 60.0)]
        public void ResolveFinalConicSettleSeconds_IsAtMostAQuarterPeriod(double period, double expected)
        {
            Assert.Equal(expected, BallisticExtrapolator.ResolveFinalConicSettleSeconds(period), 9);
        }

        [Fact]
        public void IsStableFinalConicShape_Cases()
        {
            var kerbin = new ExtrapolationBody
            {
                Name = "Kerbin",
                ParentBodyName = "Sun",
                Radius = KerbinRadius,
                DeletionAltitude = 22000.0,
                SphereOfInfluence = KerbinSoi,
            };
            // Clear of the floor, inside the sphere.
            Assert.True(BallisticExtrapolator.IsStableFinalConicShape(0.1, KerbinRadius + 30000.0, 1.0e7, 3600.0, kerbin));
            // Periapsis under the deletion altitude.
            Assert.False(BallisticExtrapolator.IsStableFinalConicShape(0.1, KerbinRadius + 20000.0, 1.0e7, 3600.0, kerbin));
            // Apoapsis beyond the sphere: it leaves.
            Assert.False(BallisticExtrapolator.IsStableFinalConicShape(0.9, KerbinRadius + 30000.0, 1.0e8, 3600.0, kerbin));
            // Open conic.
            Assert.False(BallisticExtrapolator.IsStableFinalConicShape(1.2, KerbinRadius + 30000.0, double.NaN, double.PositiveInfinity, kerbin));
            // No parent: the apoapsis does not matter.
            kerbin.ParentBodyName = null;
            Assert.True(BallisticExtrapolator.IsStableFinalConicShape(0.9, KerbinRadius + 30000.0, 1.0e8, 3600.0, kerbin));
            Assert.False(BallisticExtrapolator.IsStableFinalConicShape(0.1, KerbinRadius + 30000.0, 1.0e7, 3600.0, null));
        }

        // ------------------------------------------------------------------ finalizer

        [Fact]
        public void Finalizer_StockPatchesEndingOnASolarOrbit_EndAtTheSunEntry()
        {
            // The stock solver already captured Kerbin hyperbola -> Sun ellipse (a full period
            // long). The tail must end at the Sun entry, not a period later.
            Dictionary<string, ExtrapolationBody> bodies = SunAndKerbin();
            ExtrapolationResult path = BallisticExtrapolator.Extrapolate(
                KerbinEscapeStart(), bodies, stopOnFinalConic: false,
                limitsOverride: new ExtrapolationLimits { maxHorizonYears = 0.01, maxSoiTransitions = 2, soiSampleStep = 3600.0 });
            Assert.Equal(2, path.segments.Count);
            OrbitSegment kerbinLeg = path.segments[0];
            OrbitSegment sunLeg = path.segments[1];
            Assert.Equal("Sun", sunLeg.bodyName);
            double sunPeriod = 2.0 * Math.PI * Math.Sqrt(
                sunLeg.semiMajorAxis * sunLeg.semiMajorAxis * sunLeg.semiMajorAxis / SunGravParameter);
            sunLeg.endUT = sunLeg.startUT + sunPeriod;

            var rec = new Recording { RecordingId = "rec-sun-final", ExplicitEndUT = 0.0 };
            var snapshot = new PatchedConicSnapshotResult
            {
                FailureReason = PatchedConicSnapshotFailureReason.None,
                Segments = new List<OrbitSegment> { kerbinLeg, sunLeg },
            };

            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                snapshot,
                bodies,
                delegate(out BallisticStateVector s)
                {
                    s = default(BallisticStateVector);
                    return false;
                },
                null,
                out IncompleteBallisticFinalizationResult result);

            Assert.True(built);
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            Assert.NotNull(result.appendedOrbitSegments);
            Assert.Equal(2, result.appendedOrbitSegments.Count);
            OrbitSegment last = result.appendedOrbitSegments[1];
            Assert.Equal("Sun", last.bodyName);
            Assert.Equal(kerbinLeg.endUT, last.startUT, 6);
            Assert.Equal(last.startUT + BallisticExtrapolator.FinalConicSettleSeconds, last.endUT, 6);
            Assert.Equal(last.endUT, result.terminalUT, 6);
            Assert.True(result.terminalOrbit.HasValue);
            Assert.Equal("Sun", result.terminalOrbit.Value.bodyName);
            Assert.Equal(sunLeg.semiMajorAxis, result.terminalOrbit.Value.semiMajorAxis, 3);
            Assert.Contains(logLines, l => l.Contains("final conic of 'rec-sun-final' is the last stock patch"));
        }

        [Fact]
        public void TryMoveFinalConicEndingToPatchStart_OtherResults_ChangeNothing()
        {
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Sun", startUT = 100.0, endUT = 5000.0, semiMajorAxis = 1.0e10, eccentricity = 0.1 },
            };
            var notFinal = new ExtrapolationResult
            {
                terminalBodyName = "Sun",
                terminalUT = 5060.0,
                segments = new List<OrbitSegment>(),
                endedOnFinalConic = false,
                finalConicStartUT = 5000.0,
            };
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryMoveFinalConicEndingToPatchStart(
                "r", segments, ref notFinal, 5000.0, SunAndKerbin(), double.NaN));

            // Final conic reached after a sphere change on the way: the extrapolator's own.
            var later = notFinal;
            later.endedOnFinalConic = true;
            later.finalConicStartUT = 9000.0;
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryMoveFinalConicEndingToPatchStart(
                "r", segments, ref later, 5000.0, SunAndKerbin(), double.NaN));

            // Same instant, another body.
            var otherBody = notFinal;
            otherBody.endedOnFinalConic = true;
            otherBody.terminalBodyName = "Kerbin";
            Assert.False(IncompleteBallisticSceneExitFinalizer.TryMoveFinalConicEndingToPatchStart(
                "r", segments, ref otherBody, 5000.0, SunAndKerbin(), double.NaN));
            Assert.Equal(5000.0, segments[0].endUT);

            // Never before the recording's own end.
            var final = notFinal;
            final.endedOnFinalConic = true;
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryMoveFinalConicEndingToPatchStart(
                "r", segments, ref final, 5000.0, SunAndKerbin(), 400.0));
            Assert.Equal(400.0, segments[0].endUT);
            Assert.Equal(400.0, final.terminalUT);
            Assert.Empty(final.segments);
        }

        [Fact]
        public void TerminalOrbitFromLastSegment_OrbitingAndFinalEscape()
        {
            var final = new ExtrapolationResult { endedOnFinalConic = true };
            var capped = new ExtrapolationResult { endedOnFinalConic = false };
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsTerminalOrbitFromLastSegment(TerminalState.Orbiting, capped));
            Assert.True(IncompleteBallisticSceneExitFinalizer.IsTerminalOrbitFromLastSegment(TerminalState.SubOrbital, final));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsTerminalOrbitFromLastSegment(TerminalState.SubOrbital, capped));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsTerminalOrbitFromLastSegment(TerminalState.Destroyed, final));
            Assert.False(IncompleteBallisticSceneExitFinalizer.IsTerminalOrbitFromLastSegment(null, final));
        }

        // ------------------------------------------------------------------ owner scenario

        /// <summary>
        /// Owner scenario (V 2026-11-21): an abandoned stage on a hyperbolic Kerbin escape whose
        /// recording ends long before the sphere exit, and nobody warps there. The recording
        /// carries its last points at Kerbin up to UT 1000 and nothing else.
        /// </summary>
        private static Recording AbandonedEscapingStage()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ESCAPING");
            snapshot.AddValue("type", "Ship");
            var rec = new Recording
            {
                RecordingId = "rec-abandoned-escape",
                VesselName = "Stage Two",
                VesselPersistentId = 5151u,
                VesselSnapshot = snapshot,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 990.0, bodyName = "Kerbin", altitude = 99000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 1000.0, bodyName = "Kerbin", altitude = 100000.0 });
            return rec;
        }

        /// <summary>The stage's last state at the recording end (UT 1000): 3500 m/s at 100 km.</summary>
        private static BallisticStateVector AbandonedStageLastState()
        {
            BallisticStateVector s = KerbinEscapeStart();
            s.ut = 1000.0;
            return s;
        }

        private static void AssertSolarFinalPath(
            List<OrbitSegment> segments, double terminalUT, double recordingEndUT)
        {
            Assert.NotNull(segments);
            Assert.Equal(2, segments.Count);
            Assert.Equal("Kerbin", segments[0].bodyName);
            Assert.True(segments[0].eccentricity > 1.0, "the Kerbin leg is the escape hyperbola");
            Assert.Equal(recordingEndUT, segments[0].startUT, 6);
            Assert.Equal("Sun", segments[1].bodyName);
            Assert.True(segments[1].eccentricity < 1.0 && segments[1].semiMajorAxis > 0.0);
            Assert.Equal(segments[0].endUT, segments[1].startUT, 6);
            // The sphere exit is hours after the recording end, the ending one settle span later.
            Assert.True(segments[0].endUT - recordingEndUT > 3600.0);
            Assert.Equal(segments[1].startUT + BallisticExtrapolator.FinalConicSettleSeconds, terminalUT, 6);
        }

        [Fact]
        public void OwnerScenario_AbandonedEscapingStage_FinalizerComputesTheSolarOrbitFromTheLastState()
        {
            Recording rec = AbandonedEscapingStage();
            Dictionary<string, ExtrapolationBody> bodies = SunAndKerbin();
            var snapshot = new PatchedConicSnapshotResult
            {
                // A debris stage carries no patched-conic solver; the live orbit is the start.
                FailureReason = PatchedConicSnapshotFailureReason.NullSolver,
                Segments = new List<OrbitSegment>(),
            };

            bool built = IncompleteBallisticSceneExitFinalizer.TryCompleteFinalizationFromPatchedSnapshotForTesting(
                rec,
                snapshot,
                bodies,
                delegate(out BallisticStateVector s)
                {
                    s = AbandonedStageLastState();
                    return true;
                },
                null,
                null,
                true,
                Vector3.zero,
                out IncompleteBallisticFinalizationResult result);

            Assert.True(built);
            Assert.Equal(TerminalState.Orbiting, result.terminalState);
            AssertSolarFinalPath(result.appendedOrbitSegments, result.terminalUT, 1000.0);
            Assert.True(result.terminalOrbit.HasValue);
            Assert.Equal("Sun", result.terminalOrbit.Value.bodyName);
            Assert.Equal(result.appendedOrbitSegments[1].semiMajorAxis, result.terminalOrbit.Value.semiMajorAxis, 3);
            Assert.Equal(result.appendedOrbitSegments[1].eccentricity, result.terminalOrbit.Value.eccentricity, 9);
        }

        [Fact]
        public void OwnerScenario_AbandonedEscapingStage_OnRailsCacheAppliedThenSpawnsAroundTheSun()
        {
            Dictionary<string, ExtrapolationBody> bodies = SunAndKerbin();
            // The on-rails conic the background recorder holds at the recording end: the
            // hyperbola through the last state, built headless from that state alone.
            ExtrapolationResult conic = BallisticExtrapolator.Extrapolate(
                AbandonedStageLastState(), bodies,
                new ExtrapolationLimits { maxHorizonYears = 1.0 / BallisticExtrapolator.SecondsPerYear, maxSoiTransitions = 1, soiSampleStep = 60.0 },
                warnOnSubSurfaceStart: false, stopOnFinalConic: false);
            OrbitSegment hyperbola = conic.segments[0];
            Assert.True(hyperbola.eccentricity > 1.0);

            Assert.True(RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                "rec-abandoned-escape", 5151u, hyperbola, 1000.0,
                FinalizationCacheOwner.BackgroundOnRails, "test",
                out RecordingFinalizationCache cache, bodies));
            Assert.Equal(FinalizationCacheStatus.Fresh, cache.Status);
            Assert.Equal(TerminalState.Orbiting, cache.TerminalState);
            Assert.Equal("Sun", cache.TerminalBodyName);
            AssertSolarFinalPath(cache.PredictedSegments, cache.TerminalUT, 1000.0);
            Assert.True(cache.TerminalOrbit.HasValue);
            Assert.Equal("Sun", cache.TerminalOrbit.Value.bodyName);

            // Commit: the cache lands on the recording, which then ends at the Sun entry.
            Recording rec = AbandonedEscapingStage();
            Assert.True(RecordingFinalizationCacheApplier.TryApply(
                rec, cache, default(RecordingFinalizationCacheApplyOptions),
                out RecordingFinalizationCacheApplyResult applied), applied.Status.ToString());
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal("Sun", rec.TerminalOrbitBody);
            Assert.Equal(cache.TerminalUT, rec.EndUT, 6);
            Assert.Equal(2, rec.OrbitSegments.Count);

            // After a rewind the replay reaches that UT: the stage spawns as a real vessel
            // around the Sun, seeded from the solar conic.
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.True(needsSpawn, reason);
            Assert.True(VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(rec));
            Assert.Equal("Sun", VesselSpawner.ResolveSpawnBodyName(rec, rec.Points[rec.Points.Count - 1]));
            Assert.True(RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                rec,
                out _, out double ecc, out double sma, out _, out _, out _, out _, out string seedBody));
            Assert.Equal("Sun", seedBody);
            Assert.Equal(cache.TerminalOrbit.Value.semiMajorAxis, sma, 3);

            // The spawn safety check admits the solar orbit at its UT (stock's Sun has a
            // 600 km atmosphere; the conic is millions of km above it).
            OrbitSegment sunLeg = rec.OrbitSegments[1];
            Assert.True(BallisticExtrapolator.TryPropagate(
                sunLeg, SunGravParameter, rec.EndUT, out Vector3d spawnPos, out _));
            TerminalOrbitSpawnSafetyDecision safety = VesselSpawner.EvaluateTerminalOrbitSpawnSafetyGeometry(
                true,
                600000.0,
                Magnitude(spawnPos) - SunRadius,
                sma * (1.0 - ecc) - SunRadius,
                sma * (1.0 + ecc) - SunRadius,
                0.0,
                0.0);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, safety.Action);
        }

        // ------------------------------------------------------------------ spawn decision

        /// <summary>
        /// A capsule recorded leaving Kerbin whose finalized tail ended on a solar orbit: the
        /// shape the finalizer writes (last point at Kerbin, Kerbin hyperbola, Sun settle
        /// segment, Orbiting terminal with the solar elements).
        /// </summary>
        private static Recording SunOrbitEnding()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ESCAPING");
            snapshot.AddValue("type", "Ship");
            var rec = new Recording
            {
                RecordingId = "rec-sun-orbit",
                VesselName = "Escape Capsule",
                VesselPersistentId = 4242u,
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Orbiting,
                TerminalOrbitBody = "Sun",
                TerminalOrbitSemiMajorAxis = 1.5e10,
                TerminalOrbitEccentricity = 0.12,
                TerminalOrbitInclination = 0.0,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.3,
                TerminalOrbitEpoch = 56000.0,
                ExplicitEndUT = 56060.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin", altitude = 90000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 1000.0, bodyName = "Kerbin", altitude = 100000.0 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin", startUT = 1000.0, endUT = 56000.0,
                semiMajorAxis = -2.0e6, eccentricity = 1.35, epoch = 1000.0, isPredicted = true,
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Sun", startUT = 56000.0, endUT = 56060.0,
                semiMajorAxis = 1.5e10, eccentricity = 0.12, meanAnomalyAtEpoch = 0.3,
                epoch = 56000.0, isPredicted = true,
            });
            return rec;
        }

        [Fact]
        public void SunOrbitEnding_SpawnsAsARealVesselAroundTheSun()
        {
            Recording rec = SunOrbitEnding();

            Assert.Equal(56060.0, rec.EndUT, 6);
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.True(needsSpawn, reason);

            Assert.True(VesselSpawner.HasRecordedTerminalOrbit(rec));
            Assert.True(VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(rec));
            Assert.True(VesselSpawner.ShouldRouteThroughSpawnAtPosition(rec));
            Assert.False(Recording.IsEscapeTrajectoryTerminal(rec));

            Assert.True(RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string endpoint));
            Assert.Equal("Sun", endpoint);
            Assert.Equal("Sun", VesselSpawner.ResolveSpawnBodyName(rec, rec.Points[rec.Points.Count - 1]));
            Assert.True(RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed(
                rec,
                out double inc, out double ecc, out double sma, out double lan, out double argPe,
                out double mna, out double epoch, out string seedBody));
            Assert.Equal("Sun", seedBody);
            Assert.Equal(1.5e10, sma);
            Assert.Equal(0.12, ecc);
            Assert.Equal(56000.0, epoch);
        }
    }
}
