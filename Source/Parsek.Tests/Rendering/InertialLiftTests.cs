using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 4 inertial-longitude lift / lower tests (design doc §6.2 Stage 2,
    /// §18 Phase 4, §26.1 HR-3 / HR-9). Pure-math round-trip and degraded-
    /// mode coverage for <see cref="TrajectoryMath.FrameTransform"/>.
    /// </summary>
    /// <remarks>
    /// xUnit cannot construct fully-initialised <see cref="CelestialBody"/>
    /// instances (the live constructor depends on PQS / bodyTransform / rb
    /// state that requires the Unity engine), so the tests inject deterministic
    /// rotation-period and surface-to-world hooks via the
    /// <c>RotationPeriodForTesting</c> / <c>WorldSurfacePositionForTesting</c>
    /// seams. This is the same "uninitialised body via reflection" pattern
    /// already in use in <c>TestBodyRegistry</c>.
    ///
    /// Touches shared static state (<see cref="ParsekLog.TestSinkForTesting"/>
    /// and the seam fields) so the class runs in the Sequential collection.
    /// </remarks>
    [Collection("Sequential")]
    public class InertialLiftTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody fakeKerbin;
        private const double KerbinRotationPeriod = 21549.425; // sidereal day, seconds (KSP stock)

        public InertialLiftTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            TrajectoryMath.FrameTransform.ResetForTesting();

            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            // Default seams: synthetic Kerbin period for this body, identity
            // surface-to-world that linearly maps lat/lon/alt onto a Vector3d.
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, fakeKerbin) ? KerbinRotationPeriod : double.NaN;
            TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting =
                (b, lat, lon, alt) => new Vector3d(lat, lon, alt);
        }

        public void Dispose()
        {
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- RotationAngleAtUT ---

        [Fact]
        public void RotationAngleAtUT_KerbinSiderealDay_ReturnsThreeSixty()
        {
            // What makes it fail: phase formula scaled wrong (e.g. radians vs
            // degrees, or rotationPeriod inverted) — at one full sidereal day
            // the body must have advanced exactly 360 deg.
            double angle = TrajectoryMath.FrameTransform.RotationAngleAtUT(fakeKerbin, KerbinRotationPeriod);
            Assert.Equal(360.0, angle, precision: 9);
        }

        [Fact]
        public void RotationAngleAtUT_QuarterDay_ReturnsNinetyDegrees()
        {
            // What makes it fail: scaling regression. UT = period/4 must yield
            // exactly 90 deg of phase, not 0.25 or 90/period.
            double angle = TrajectoryMath.FrameTransform.RotationAngleAtUT(fakeKerbin, KerbinRotationPeriod / 4.0);
            Assert.Equal(90.0, angle, precision: 9);
        }

        [Fact]
        public void RotationAngleAtUT_NullBody_ReturnsZero()
        {
            // What makes it fail: HR-9 — passing null must not throw and must
            // produce a no-op zero phase rather than a NullRef.
            double angle = TrajectoryMath.FrameTransform.RotationAngleAtUT(null, 1234.5);
            Assert.Equal(0.0, angle);
        }

        // --- LiftToInertial ---

        [Fact]
        public void LiftToInertial_AtRecordedUT_AdvancesLongitudeByRotationAngle()
        {
            // What makes it fail: lift sign error — body-fixed → inertial must
            // ADD the rotation phase (a body-fixed point sweeps eastward in
            // inertial space as the body rotates), not subtract it.
            const double lat = 12.5;
            const double lon = 30.0;
            const double alt = 80000.0;
            double recordedUT = KerbinRotationPeriod / 4.0;

            Vector3d lifted = TrajectoryMath.FrameTransform.LiftToInertial(
                lat, lon, alt, fakeKerbin, recordedUT);

            Assert.Equal(lat, lifted.x, precision: 9);
            Assert.Equal(120.0, lifted.y, precision: 9); // 30 + 90
            Assert.Equal(alt, lifted.z, precision: 6);
        }

        [Fact]
        public void LiftToInertial_LongitudeWrappedToCanonicalRange()
        {
            // What makes it fail: forgetting the WrapLongitude pass — phase >
            // 180 deg pushes inertial longitude outside (-180, 180] and any
            // downstream code that assumes the canonical range silently mis-
            // interpolates.
            // Lift at 3/4 day → +270 deg phase; lon=30 + 270 = 300 → wraps to -60.
            Vector3d lifted = TrajectoryMath.FrameTransform.LiftToInertial(
                0.0, 30.0, 0.0, fakeKerbin, KerbinRotationPeriod * 0.75);
            Assert.True(lifted.y > -180.0 && lifted.y <= 180.0,
                $"inertial longitude {lifted.y} not in (-180, 180]");
            Assert.Equal(-60.0, lifted.y, precision: 9);
        }

        [Fact]
        public void LiftToInertial_NullBody_ReturnsBodyFixed_LogsWarn()
        {
            // What makes it fail: HR-9 visible-failure contract. A null body
            // must not throw; it must downgrade to body-fixed identity AND
            // surface a Pipeline-Frame Warn so the regression appears in
            // KSP.log instead of producing a "plausible but wrong" lift.
            Vector3d lifted = TrajectoryMath.FrameTransform.LiftToInertial(
                12.5, 30.0, 80000.0, null, recordedUT: 1000.0);

            Assert.Equal(12.5, lifted.x, precision: 9);
            Assert.Equal(30.0, lifted.y, precision: 9);
            Assert.Equal(80000.0, lifted.z, precision: 6);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Frame]")
                && l.Contains("LiftToInertial degraded to body-fixed")
                && l.Contains("body=null"));
        }

        [Fact]
        public void LiftToInertial_ZeroRotationPeriod_ReturnsBodyFixed_LogsWarn()
        {
            // What makes it fail: HR-9 — tidally-locked bodies (zero / NaN /
            // Inf rotation period) must downgrade to body-fixed identity and
            // log Warn. Without this guard, a divide-by-zero or NaN propagates
            // through the spline and silently corrupts the ghost.
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b => 0.0;
            Vector3d lifted = TrajectoryMath.FrameTransform.LiftToInertial(
                12.5, 30.0, 80000.0, fakeKerbin, recordedUT: 1000.0);
            Assert.Equal(30.0, lifted.y, precision: 9);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Frame]")
                && l.Contains("LiftToInertial degraded to body-fixed")
                && l.Contains("rotationPeriod=0"));

            // NaN period: same Warn behaviour.
            logLines.Clear();
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b => double.NaN;
            lifted = TrajectoryMath.FrameTransform.LiftToInertial(
                12.5, 30.0, 80000.0, fakeKerbin, recordedUT: 1000.0);
            Assert.Equal(30.0, lifted.y, precision: 9);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Frame]")
                && l.Contains("LiftToInertial degraded to body-fixed"));

            // Infinity period: same.
            logLines.Clear();
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b => double.PositiveInfinity;
            lifted = TrajectoryMath.FrameTransform.LiftToInertial(
                12.5, 30.0, 80000.0, fakeKerbin, recordedUT: 1000.0);
            Assert.Equal(30.0, lifted.y, precision: 9);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Frame]")
                && l.Contains("LiftToInertial degraded to body-fixed"));
        }

        // --- LiftLowerRoundTrip ---

        [Fact]
        public void LiftLowerRoundTrip_SameUT_BitExact()
        {
            // What makes it fail: HR-3 determinism — Lift(p, ut) then
            // Lower(p, ut) at the SAME UT must produce the same world position
            // as a direct GetWorldSurfacePosition call. Any sign / scaling
            // mismatch surfaces as a non-trivial residual.
            const double lat = 12.5;
            const double lon = 30.0;
            const double alt = 80000.0;
            const double ut = 5000.0;

            Vector3d lifted = TrajectoryMath.FrameTransform.LiftToInertial(lat, lon, alt, fakeKerbin, ut);
            Vector3d roundTrip = TrajectoryMath.FrameTransform.LowerFromInertialToWorld(
                lifted.x, lifted.y, lifted.z, fakeKerbin, ut);
            Vector3d direct = TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting(
                fakeKerbin, lat, lon, alt);

            Assert.Equal(direct.x, roundTrip.x, precision: 6);
            Assert.Equal(direct.y, roundTrip.y, precision: 6);
            Assert.Equal(direct.z, roundTrip.z, precision: 6);
        }

        [Fact]
        public void LiftLowerRoundTrip_AcrossUTs_RotatesByDelta()
        {
            // What makes it fail: lift/lower decoupling — Lift at UT1, Lower
            // at UT2 must produce the SAME world point as if we took the raw
            // body-fixed value, subtracted the (UT2 - UT1) rotation, and
            // looked it up directly. This is the actual physics Phase 4
            // exists to model: a recorded coast point at UT1 is rendered at
            // UT2's body orientation, with the body's rotation in between.
            const double lat = 12.5;
            const double lon = 30.0;
            const double alt = 80000.0;
            const double ut1 = 5000.0;
            const double ut2 = ut1 + KerbinRotationPeriod / 4.0; // body advanced 90 deg

            Vector3d lifted = TrajectoryMath.FrameTransform.LiftToInertial(lat, lon, alt, fakeKerbin, ut1);
            Vector3d lowered = TrajectoryMath.FrameTransform.LowerFromInertialToWorld(
                lifted.x, lifted.y, lifted.z, fakeKerbin, ut2);

            // Inertial longitude at UT1 = lon + ut1*360/period.
            // Body-fixed longitude at UT2 = inertialLon - ut2*360/period
            //                             = lon + (ut1 - ut2)*360/period
            //                             = lon - 90.
            double expectedBodyFixedLon = lon - 90.0;
            // wrap into (-180, 180]
            if (expectedBodyFixedLon > 180.0) expectedBodyFixedLon -= 360.0;
            else if (expectedBodyFixedLon <= -180.0) expectedBodyFixedLon += 360.0;
            Vector3d expected = TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting(
                fakeKerbin, lat, expectedBodyFixedLon, alt);

            Assert.Equal(expected.x, lowered.x, precision: 6);
            Assert.Equal(expected.y, lowered.y, precision: 6);
            Assert.Equal(expected.z, lowered.z, precision: 6);
        }

        [Fact]
        public void LowerFromInertialToWorld_NullBody_ReturnsZero_LogsWarn()
        {
            // What makes it fail: HR-9 — null body must short-circuit before
            // dereferencing the live GetWorldSurfacePosition path. The Warn
            // surfaces the failure in KSP.log; the zero return value lets the
            // caller's NaN guard fall through to the body-fixed lerp.
            Vector3d lowered = TrajectoryMath.FrameTransform.LowerFromInertialToWorld(
                12.5, 30.0, 80000.0, null, playbackUT: 1000.0);
            Assert.Equal(0.0, lowered.x, precision: 9);
            Assert.Equal(0.0, lowered.y, precision: 9);
            Assert.Equal(0.0, lowered.z, precision: 9);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Frame]")
                && l.Contains("LowerFromInertialToWorld degraded to zero")
                && l.Contains("body=null"));
        }

        // --- Integration: spline-eval round-trip ---

        [Fact]
        public void EvaluateInertialSpline_RoundTripsBodyFixedPositionAtRecordingUT()
        {
            // What makes it fail: lift/lower sign mismatch. If the spline is
            // fitted with longitudes ADVANCED by recording-UT phase but the
            // consumer subtracts the WRONG sign at playback, the rendered
            // ghost lands twice the rotation phase off. At the recording UT
            // the spline knot value bit-equals the raw input, so a clean
            // lift/lower round-trip must recover the original body-fixed
            // world position. Tolerance 0.1 m per task spec.
            const double recordingUT = 12345.0;
            // Build 6 frames spanning a few seconds at the recording UT,
            // simulating an orbital coast.
            var frames = new List<TrajectoryPoint>();
            for (int i = 0; i < 6; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = recordingUT + i * 0.5,
                    latitude = 0.5 + i * 0.001,
                    longitude = 30.0 + i * 0.0001,
                    altitude = 80000 + i * 1.0,
                    rotation = Quaternion.identity,
                    bodyName = "Kerbin",
                });
            }

            // Lift each frame to inertial-longitude space (Task 3 production
            // path) and fit there.
            var lifted = new List<TrajectoryPoint>(frames.Count);
            foreach (var p in frames)
            {
                Vector3d li = TrajectoryMath.FrameTransform.LiftToInertial(
                    p.latitude, p.longitude, p.altitude, fakeKerbin, p.ut);
                lifted.Add(new TrajectoryPoint
                {
                    ut = p.ut,
                    latitude = li.x,
                    longitude = li.y,
                    altitude = li.z,
                    rotation = p.rotation,
                    bodyName = p.bodyName,
                });
            }

            SmoothingSpline spline = TrajectoryMath.CatmullRomFit.Fit(
                lifted, tension: 0.5, out _);
            Assert.True(spline.IsValid);

            // Evaluate the inertial spline at a frame's recording UT, then
            // lower at the same UT — must equal a direct world lookup of the
            // raw body-fixed sample. Pick i=2 (interior, not an endpoint).
            int probeIdx = 2;
            double probeUT = frames[probeIdx].ut;
            Vector3d inertialLatLonAlt = TrajectoryMath.CatmullRomFit.Evaluate(spline, probeUT);
            Vector3d roundTrip = TrajectoryMath.FrameTransform.LowerFromInertialToWorld(
                inertialLatLonAlt.x, inertialLatLonAlt.y, inertialLatLonAlt.z,
                fakeKerbin, probeUT);
            Vector3d direct = TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting(
                fakeKerbin, frames[probeIdx].latitude, frames[probeIdx].longitude, frames[probeIdx].altitude);

            Assert.True(Math.Abs(direct.x - roundTrip.x) < 0.1,
                $"x residual {direct.x - roundTrip.x} exceeds 0.1 m");
            Assert.True(Math.Abs(direct.y - roundTrip.y) < 0.1,
                $"y residual {direct.y - roundTrip.y} exceeds 0.1 m");
            Assert.True(Math.Abs(direct.z - roundTrip.z) < 0.1,
                $"z residual {direct.z - roundTrip.z} exceeds 0.1 m");
        }
    }
}
