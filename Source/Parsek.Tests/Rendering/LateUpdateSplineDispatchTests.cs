using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure"/>
    /// — the LateUpdate spline re-evaluation gate (design doc §6.1 / §6.2 /
    /// §18 Phase 1+4 / §26.1 HR-9). xUnit cannot drive Unity's LateUpdate
    /// callback, so these tests pin the static gate's branches without
    /// standing up a live <c>GhostPosEntry</c>; the integration that proves
    /// LateUpdate actually calls through to the gate is covered by the
    /// in-game <c>Pipeline_Smoothing_NoJitterOnCoast</c> test.
    /// <para>
    /// The bug this gate fixes: before the fix, LateUpdate's PointInterp
    /// branch unconditionally rebuilt the ghost's position from the raw
    /// (latBefore, latAfter) bracket and overwrote whatever the Update path
    /// had spline-positioned — so smoothing visibly disappeared every other
    /// frame.
    /// </para>
    /// <para>
    /// Touches shared static state (<see cref="SectionAnnotationStore"/>,
    /// <see cref="ParsekSettings.CurrentOverrideForTesting"/>,
    /// <see cref="TrajectoryMath.FrameTransform"/> seams,
    /// <see cref="ParsekLog.TestSinkForTesting"/>), so runs in the
    /// <c>Sequential</c> collection.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class LateUpdateSplineDispatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CelestialBody fakeKerbin;
        private const double KerbinRotationPeriod = 21549.425;

        public LateUpdateSplineDispatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            SectionAnnotationStore.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                useSmoothingSplines = true,
            };

            fakeKerbin = TestBodyRegistry.CreateBody("Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            CelestialBody capturedKerbin = fakeKerbin;
            // Identity surface-to-world maps (lat, lon, alt) -> Vector3d so
            // tests can assert exact world positions. RotationPeriod is the
            // Kerbin sidereal day, used by the inertial-lower path.
            TrajectoryMath.FrameTransform.WorldSurfacePositionForTesting =
                (b, lat, lon, alt) => new Vector3d(lat, lon, alt);
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, capturedKerbin) ? KerbinRotationPeriod : double.NaN;
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            SectionAnnotationStore.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- helpers -------------------------------------------------------

        private static SmoothingSpline MakeConstantSpline(byte frameTag, double ut, Vector3d valueLatLonAlt)
        {
            // 4 knots, every control = the same value, so Evaluate(ut) == value
            // regardless of where ut lands inside the knot range. This makes
            // the FrameTag dispatch the only thing that varies the result.
            int kc = 4;
            double[] knots = new double[kc];
            float[] cx = new float[kc];
            float[] cy = new float[kc];
            float[] cz = new float[kc];
            for (int i = 0; i < kc; i++)
            {
                knots[i] = ut - 1.0 + i;
                cx[i] = (float)valueLatLonAlt.x;
                cy[i] = (float)valueLatLonAlt.y;
                cz[i] = (float)valueLatLonAlt.z;
            }
            return new SmoothingSpline
            {
                SplineType = 0,
                Tension = 0.5f,
                KnotsUT = knots,
                ControlsX = cx,
                ControlsY = cy,
                ControlsZ = cz,
                FrameTag = frameTag,
                IsValid = true,
            };
        }

        // --- gate-failure branches -----------------------------------------

        [Fact]
        public void Pure_NullRecordingId_ReturnsFalse()
        {
            // What makes it fail: an empty/null recordingId means the
            // registration-time guard didn't thread a recording context, so
            // the consumer must short-circuit and let the raw-lerp fallback
            // take over. A buggy gate that asks the store with an empty key
            // would either throw or return a phantom spline.
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: null, sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out Vector3d worldPos);
            Assert.False(result);
            Assert.Equal(0.0, worldPos.x);
            Assert.Equal(0.0, worldPos.y);
            Assert.Equal(0.0, worldPos.z);
        }

        [Fact]
        public void Pure_NegativeSectionIndex_ReturnsFalse()
        {
            // What makes it fail: -1 is the canonical "no section" sentinel
            // (see InterpolateAndPosition's TrajectoryMath.FindTrackSectionForUT
            // miss path). The gate must reject it — store keys are non-negative.
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: -1, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out _);
            Assert.False(result);
        }

        [Fact]
        public void Pure_NullSettings_ReturnsFalse()
        {
            // What makes it fail: ParsekSettings.Current is null between
            // scene transitions; the gate must not throw, just fall through.
            ParsekSettings.CurrentOverrideForTesting = null;
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 0, ut: 100.0, valueLatLonAlt: new Vector3d(1, 2, 3)));
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out _);
            Assert.False(result);
        }

        [Fact]
        public void Pure_FlagOff_ReturnsFalse()
        {
            // What makes it fail: rollout flag toggles must short-circuit the
            // gate before the store lookup. Otherwise an operator disabling
            // useSmoothingSplines mid-session would still see Phase 1 effects.
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { useSmoothingSplines = false };
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 0, ut: 100.0, valueLatLonAlt: new Vector3d(1, 2, 3)));
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out _);
            Assert.False(result);
        }

        [Fact]
        public void Pure_NoSplineInStore_ReturnsFalse()
        {
            // What makes it fail: no spline stored = legitimate "lazy compute
            // hasn't run yet" state; gate must fall through silently (HR-9
            // explicitly classifies this as a normal state, not failure).
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec-empty", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out _);
            Assert.False(result);
        }

        [Fact]
        public void Pure_InvalidSpline_ReturnsFalse()
        {
            // What makes it fail: a stored spline with IsValid=false (degenerate
            // fit) must not be consumed — IsValid is the seam between "we have
            // data" and "we have a usable spline." The store doesn't filter on
            // it; the gate does.
            var bad = MakeConstantSpline(frameTag: 0, ut: 100.0, valueLatLonAlt: Vector3d.zero);
            bad.IsValid = false;
            SectionAnnotationStore.PutSmoothingSpline("rec-bad", 0, bad);
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec-bad", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out _);
            Assert.False(result);
        }

        [Fact]
        public void Pure_NullBody_ReturnsFalse()
        {
            // What makes it fail: the LateUpdate caller dereferences body via
            // GetWorldSurfacePosition or via FrameTransform.LowerFromInertialToWorld.
            // A null body must be rejected before either dispatch runs.
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 0, ut: 100.0, valueLatLonAlt: new Vector3d(1, 2, 3)));
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: 100.0, body: null,
                unknownFrameTagWarnedKeys: null, out _);
            Assert.False(result);
        }

        // --- happy paths ---------------------------------------------------

        [Fact]
        public void Pure_FrameTag0_DispatchesBodyFixed()
        {
            // What makes it fail: P1#1 root cause — without LateUpdate
            // re-evaluation, the spline-positioned value is discarded every
            // late frame. With body-fixed FrameTag=0, the dispatch must hand
            // (lat, lon, alt) straight to the surface-lookup seam.
            Vector3d expected = new Vector3d(0.5, 1.5, 80100);
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 0, ut: 100.0, valueLatLonAlt: expected));

            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out Vector3d worldPos);
            Assert.True(result);
            // Identity surface-lookup seam means worldPos == (lat, lon, alt).
            Assert.Equal(expected.x, worldPos.x, 6);
            Assert.Equal(expected.y, worldPos.y, 6);
            Assert.Equal(expected.z, worldPos.z, 6);
        }

        [Fact]
        public void Pure_FrameTag1_DispatchesInertial()
        {
            // What makes it fail: Phase 4 inertial path — with FrameTag=1, the
            // dispatch must go through LowerFromInertialToWorld, which subtracts
            // the body's rotation phase at playbackUT from the inertial
            // longitude before the surface lookup. A bug that skipped the
            // lower would render at the wrong longitude (off by one full sidereal
            // rotation per 21549 s of UT skew).
            //
            // Construct a spline whose evaluated value is a known inertial
            // longitude. At pointUT = period/4 the phase is 90deg, so the
            // body-fixed longitude after lower = inertialLon - 90.
            double playbackUT = KerbinRotationPeriod / 4.0;
            Vector3d inertialLatLonAlt = new Vector3d(1.0, 100.0, 80000.0);
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 1, ut: playbackUT, valueLatLonAlt: inertialLatLonAlt));

            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: playbackUT, body: fakeKerbin,
                unknownFrameTagWarnedKeys: null, out Vector3d worldPos);
            Assert.True(result);
            // expected body-fixed lon = 100 - 90 = 10; identity surface-lookup
            // means worldPos.y == 10.
            Assert.Equal(1.0, worldPos.x, 6);
            Assert.Equal(10.0, worldPos.y, 6);
            Assert.Equal(80000.0, worldPos.z, 6);
        }

        // --- unknown FrameTag -----------------------------------------------

        [Fact]
        public void Pure_UnknownFrameTag_ReturnsFalse()
        {
            // What makes it fail: an unrecognised tag must NOT silently render
            // at body-fixed coordinates (HR-9 visible failure). The dispatch
            // returns NaN, the gate sees NaN and falls through to the raw
            // lerp. The Warn dedup is shared with the Update path so we don't
            // re-emit; passing a fresh empty set verifies the line still fires
            // when not yet deduplicated.
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 99, ut: 100.0, valueLatLonAlt: new Vector3d(1, 2, 3)));
            var dedup = new HashSet<string>(StringComparer.Ordinal);
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: dedup, out _);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Smoothing]")
                && l.Contains("unknown frameTag=99")
                && l.Contains("recordingId=rec"));
        }

        [Fact]
        public void Pure_UnknownFrameTag_WarnDedupSuppressesLateUpdateRepeat()
        {
            // What makes it fail: LateUpdate runs after Update in the same
            // frame; passing a dedup set already populated by the Update
            // path's earlier dispatch must suppress the LateUpdate's Warn so
            // KSP.log doesn't double per-frame. The gate must still return
            // false (dispatch returns NaN regardless of the Warn emit).
            SectionAnnotationStore.PutSmoothingSpline("rec", 0,
                MakeConstantSpline(frameTag: 99, ut: 100.0, valueLatLonAlt: new Vector3d(1, 2, 3)));
            // Pre-seed the dedup set as if the Update path already warned.
            var dedup = new HashSet<string>(StringComparer.Ordinal) { "rec:0" };
            bool result = ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                recordingId: "rec", sectionIndex: 0, pointUT: 100.0, body: fakeKerbin,
                unknownFrameTagWarnedKeys: dedup, out _);
            Assert.False(result);
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][Pipeline-Smoothing]")
                && l.Contains("unknown frameTag=99"));
        }
    }
}
