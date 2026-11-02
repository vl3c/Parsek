using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the pure load-time member reconcile helpers of
    /// <see cref="BackgroundRecorder"/> that no test called directly: the dropped-member
    /// terminal resolver, the missing-members summary line and the drift-check throttle.
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundRecorderCharacterizationTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;

        public BackgroundRecorderCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting =
                n => n == "Kerbin" ? KerbinRadius : (double?)null;
            ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = null;
        }

        public void Dispose()
        {
            ParsekFlight.TerminalInferenceBodyRadiusResolverForTesting = null;
            ParsekFlight.TerminalInferencePeriapsisFloorResolverForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        private static OrbitSegment Orbit(double start, double end, double sma, double ecc) =>
            new OrbitSegment
            {
                startUT = start,
                endUT = end,
                bodyName = "Kerbin",
                semiMajorAxis = sma,
                eccentricity = ecc
            };

        private static TrajectoryPoint Pt(double ut, double altitude) =>
            new TrajectoryPoint { ut = ut, altitude = altitude, bodyName = "Kerbin" };

        // ---------- ResolveDroppedMemberTerminal --------------------------------

        [Fact]
        public void ResolveDroppedMemberTerminal_NullOrEmpty_SubOrbitalNoTrajectory()
        {
            Assert.Equal(TerminalState.SubOrbital,
                BackgroundRecorder.ResolveDroppedMemberTerminal(null, out string evidence));
            Assert.Equal("no-trajectory", evidence);

            Assert.Equal(TerminalState.SubOrbital,
                BackgroundRecorder.ResolveDroppedMemberTerminal(new Recording(), out evidence));
            Assert.Equal("no-trajectory", evidence);
        }

        [Fact]
        public void ResolveDroppedMemberTerminal_LastOrbitCoversLastPoint_StableIsOrbiting()
        {
            var rec = new Recording();
            rec.Points.Add(Pt(100.0, 80000.0));
            rec.OrbitSegments.Add(Orbit(100.0, 500.0, 700000.0, 0.01));
            Assert.Equal(TerminalState.Orbiting,
                BackgroundRecorder.ResolveDroppedMemberTerminal(rec, out string evidence));
            Assert.Equal("last-orbit-segment", evidence);
        }

        [Fact]
        public void ResolveDroppedMemberTerminal_LastOrbitCoversLastPoint_UnstableIsSubOrbital()
        {
            var hyperbolic = new Recording();
            hyperbolic.OrbitSegments.Add(Orbit(100.0, 500.0, -700000.0, 1.2));
            Assert.Equal(TerminalState.SubOrbital,
                BackgroundRecorder.ResolveDroppedMemberTerminal(hyperbolic, out string evidence));
            Assert.Equal("last-orbit-segment-unstable", evidence);

            // Periapsis inside the body: not clear of the floor.
            var grazing = new Recording();
            grazing.OrbitSegments.Add(Orbit(100.0, 500.0, 400000.0, 0.1));
            Assert.Equal(TerminalState.SubOrbital,
                BackgroundRecorder.ResolveDroppedMemberTerminal(grazing, out evidence));
            Assert.Equal("last-orbit-segment-unstable", evidence);
        }

        [Fact]
        public void ResolveDroppedMemberTerminal_SurfaceCaptureWithoutPoints()
        {
            var splashed = new Recording
            {
                SurfacePos = new SurfacePosition { body = "Kerbin", situation = SurfaceSituation.Splashed }
            };
            Assert.Equal(TerminalState.Splashed,
                BackgroundRecorder.ResolveDroppedMemberTerminal(splashed, out string evidence));
            Assert.Equal("surface-position", evidence);

            var landed = new Recording
            {
                SurfacePos = new SurfacePosition { body = "Kerbin", situation = SurfaceSituation.Landed }
            };
            Assert.Equal(TerminalState.Landed,
                BackgroundRecorder.ResolveDroppedMemberTerminal(landed, out evidence));
            Assert.Equal("surface-position", evidence);
        }

        [Fact]
        public void ResolveDroppedMemberTerminal_OrbitEndingBeforeLastPoint_UsesPointInference()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(Orbit(0.0, 50.0, 700000.0, 0.01));
            rec.Points.Add(Pt(60.0, 5000.0));
            rec.Points.Add(Pt(100.0, 10.0));
            // Last point below 50 m reads as Landed.
            Assert.Equal(TerminalState.Landed,
                BackgroundRecorder.ResolveDroppedMemberTerminal(rec, out string evidence));
            Assert.Equal("last-point", evidence);
        }

        [Fact]
        public void ResolveDroppedMemberTerminal_PointsPlusSurface_SurfaceRefinesInference()
        {
            var airborne = new Recording
            {
                SurfacePos = new SurfacePosition { body = "Kerbin", situation = SurfaceSituation.Splashed }
            };
            airborne.Points.Add(Pt(100.0, 5000.0));
            // Inference says SubOrbital; the surface capture stands in for it.
            Assert.Equal(TerminalState.Splashed,
                BackgroundRecorder.ResolveDroppedMemberTerminal(airborne, out string evidence));
            Assert.Equal("last-point+surface-position", evidence);

            var noSurface = new Recording();
            noSurface.Points.Add(Pt(100.0, 5000.0));
            Assert.Equal(TerminalState.SubOrbital,
                BackgroundRecorder.ResolveDroppedMemberTerminal(noSurface, out evidence));
            Assert.Equal("last-point", evidence);
        }

        // ---------- FormatMissingBackgroundMembersSummary -----------------------

        [Fact]
        public void FormatMissingBackgroundMembersSummary_PlannedUnplannedAndKept()
        {
            var missing = new List<BackgroundRecorder.MissingBackgroundMember>
            {
                new BackgroundRecorder.MissingBackgroundMember { VesselPid = 11, RecordingId = "r1", IsDebris = true },
                new BackgroundRecorder.MissingBackgroundMember { VesselPid = 22, RecordingId = null },
                new BackgroundRecorder.MissingBackgroundMember { VesselPid = 33, RecordingId = "r3" },
            };
            var plans = new List<BackgroundRecorder.MissingMemberClosePlan>
            {
                new BackgroundRecorder.MissingMemberClosePlan
                {
                    Outcome = BackgroundRecorder.MissingMemberCloseOutcome.LeftToTerminalEvent,
                    Terminal = TerminalState.Recovered,
                    EndUT = 120.0,
                    Evidence = "ignored"
                },
                new BackgroundRecorder.MissingMemberClosePlan
                {
                    Outcome = BackgroundRecorder.MissingMemberCloseOutcome.ClosedAtLastKnownUT,
                    Terminal = TerminalState.SubOrbital,
                    EndUT = double.NaN,
                    Evidence = "last-point"
                },
            };

            string line = BackgroundRecorder.FormatMissingBackgroundMembersSummary(missing, plans, 5, "unit");

            Assert.Equal(
                "Load check: 3 of 5 background member(s) have no vessel in the loaded scene " +
                "(debris=1; likely dropped by the stock vessel budget or KSC declutter at save): " +
                "reason=unit pids=[11,22,33] recIds=[r1,(null),r3] - closed=1 " +
                "(situation terminal at last known UT, no Destroyed) keptTerminal=1 " +
                "(already ended by a recovery/termination event) " +
                "outcomes=[11:kept-Recovered@120.0,22:SubOrbital@NaN(last-point),unplanned]",
                line);
        }

        [Fact]
        public void FormatMissingBackgroundMembersSummary_NullInputs_ZeroCountsAndUnspecifiedReason()
        {
            string line = BackgroundRecorder.FormatMissingBackgroundMembersSummary(null, null, 4, null);
            Assert.StartsWith("Load check: 0 of 4 background member(s)", line);
            Assert.Contains("(debris=0;", line);
            Assert.Contains("reason=unspecified pids=[] recIds=[] - closed=0 ", line);
            Assert.Contains("keptTerminal=0 ", line);
            Assert.EndsWith("outcomes=[]", line);
        }

        // ---------- ShouldRunBackgroundStateDriftCheck --------------------------

        [Fact]
        public void ShouldRunBackgroundStateDriftCheck_FirstCallRuns()
        {
            Assert.True(BackgroundRecorder.ShouldRunBackgroundStateDriftCheck(
                double.MinValue, 42.0, out double next));
            Assert.Equal(42.0, next);
        }

        [Fact]
        public void ShouldRunBackgroundStateDriftCheck_ThrottledUntilFiveSecondsPass()
        {
            Assert.False(BackgroundRecorder.ShouldRunBackgroundStateDriftCheck(100.0, 104.9, out double next));
            Assert.Equal(100.0, next);
            Assert.True(BackgroundRecorder.ShouldRunBackgroundStateDriftCheck(100.0, 105.0, out next));
            Assert.Equal(105.0, next);
        }

        [Fact]
        public void ShouldRunBackgroundStateDriftCheck_TimeWentBackwards_RunsAtOnce()
        {
            Assert.True(BackgroundRecorder.ShouldRunBackgroundStateDriftCheck(100.0, 50.0, out double next));
            Assert.Equal(50.0, next);
        }
    }
}
