using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostRenderTraceAttitudeResidualTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostRenderTraceAttitudeResidualTests()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = () => 42;
            GhostRenderTrace.ExpectedAttitudeSource = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = null;
            GhostRenderTrace.ExpectedAttitudeSource = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        // Axis-angle built by hand: Quaternion.AngleAxis is a Unity ECall.
        private static Quaternion AxisAngle(double degrees, double x, double y, double z)
        {
            double n = Math.Sqrt(x * x + y * y + z * z);
            double half = degrees * Math.PI / 360.0;
            double s = Math.Sin(half) / n;
            return new Quaternion((float)(x * s), (float)(y * s), (float)(z * s), (float)Math.Cos(half));
        }

        private static Quaternion Mul(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        [Fact]
        public void RotationResidual_IdenticalRotations_IsZero()
        {
            Quaternion q = AxisAngle(73.0, 0.3, -1.0, 0.4);
            Assert.Equal(0.0, GhostRenderTrace.RotationResidualDegrees(q, q), 6);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(10.0)]
        [InlineData(90.0)]
        [InlineData(179.0)]
        public void RotationResidual_KnownOffset_MeasuresThatAngle(double degrees)
        {
            Quaternion baseRot = AxisAngle(41.0, 1.0, 2.0, -0.5);
            Quaternion offset = AxisAngle(degrees, -0.2, 0.7, 1.0);
            // The residual is the angle of the offset whichever side it is applied on.
            Assert.Equal(degrees, GhostRenderTrace.RotationResidualDegrees(baseRot, Mul(baseRot, offset)), 3);
            Assert.Equal(degrees, GhostRenderTrace.RotationResidualDegrees(baseRot, Mul(offset, baseRot)), 3);
        }

        [Fact]
        public void RotationResidual_DoubleCover_NegatedQuaternionIsSameAttitude()
        {
            Quaternion q = AxisAngle(120.0, 0.0, 1.0, 0.0);
            var negated = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            Assert.Equal(0.0, GhostRenderTrace.RotationResidualDegrees(q, negated), 6);
        }

        [Fact]
        public void RotationResidual_ScaledInput_IsNormalized()
        {
            Quaternion q = AxisAngle(30.0, 1.0, 0.0, 0.0);
            var scaled = new Quaternion(q.x * 3f, q.y * 3f, q.z * 3f, q.w * 3f);
            Quaternion other = Mul(q, AxisAngle(20.0, 0.0, 0.0, 1.0));
            Assert.Equal(20.0, GhostRenderTrace.RotationResidualDegrees(scaled, other), 3);
        }

        [Fact]
        public void RotationResidual_SmallAngle_KeepsSubDegreePrecision()
        {
            // The acos(dot) form cannot resolve this from float inputs; atan2 on the
            // relative quaternion does.
            Quaternion q = AxisAngle(200.0, 0.1, 0.9, -0.3);
            Quaternion r = Mul(q, AxisAngle(0.05, 1.0, 0.0, 0.0));
            Assert.InRange(GhostRenderTrace.RotationResidualDegrees(q, r), 0.04, 0.06);
        }

        [Fact]
        public void RotationResidual_DegenerateInput_IsNaN()
        {
            Quaternion q = AxisAngle(10.0, 1.0, 0.0, 0.0);
            Assert.True(double.IsNaN(GhostRenderTrace.RotationResidualDegrees(new Quaternion(0f, 0f, 0f, 0f), q)));
            Assert.True(double.IsNaN(GhostRenderTrace.RotationResidualDegrees(q, new Quaternion(float.NaN, 0f, 0f, 1f))));
            Assert.True(double.IsNaN(GhostRenderTrace.RotationResidualDegrees(q, new Quaternion(float.PositiveInfinity, 0f, 0f, 1f))));
        }

        [Fact]
        public void FormatAttitudeResidualFields_IsCultureInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal(" dRotDeg=12.346 rotRef=checkpoint-orbit-ofr",
                    GhostRenderTrace.FormatAttitudeResidualFields(12.3456, "checkpoint-orbit-ofr"));
                Assert.Equal(" dRotDeg=NaN rotRef=not-rendered",
                    GhostRenderTrace.FormatAttitudeResidualFields(double.NaN, "not-rendered"));
                Assert.Equal(" dRotDeg=0.000 rotRef=<none>",
                    GhostRenderTrace.FormatAttitudeResidualFields(0.0, null));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ResolveResidual_NotRendered_SkipsResolver()
        {
            bool called = false;
            GhostRenderTrace.ExpectedAttitudeSource = (IPlaybackTrajectory t, GhostPlaybackState s, double ut,
                GhostRenderTrace.RenderSurface surf, out Quaternion e, out string r) =>
            {
                called = true;
                e = Quaternion.identity;
                r = "surface";
                return true;
            };

            double residual = GhostRenderTrace.ResolveAttitudeResidualDegrees(
                new MockTrajectory { RecordingId = "r" }, null, 10.0,
                GhostRenderTrace.RenderSurface.Legacy, rendered: false,
                renderedRotation: Quaternion.identity, out string rotRef);

            Assert.False(called);
            Assert.True(double.IsNaN(residual));
            Assert.Equal("not-rendered", rotRef);
        }

        [Fact]
        public void ResolveResidual_NoResolver_IsNaNWithReason()
        {
            double residual = GhostRenderTrace.ResolveAttitudeResidualDegrees(
                new MockTrajectory { RecordingId = "r" }, null, 10.0,
                GhostRenderTrace.RenderSurface.Legacy, rendered: true,
                renderedRotation: Quaternion.identity, out string rotRef);
            Assert.True(double.IsNaN(residual));
            Assert.Equal("no-resolver", rotRef);
        }

        [Fact]
        public void ResolveResidual_ResolverDeclines_CarriesItsReason()
        {
            GhostRenderTrace.ExpectedAttitudeSource = (IPlaybackTrajectory t, GhostPlaybackState s, double ut,
                GhostRenderTrace.RenderSurface surf, out Quaternion e, out string r) =>
            {
                e = Quaternion.identity;
                r = "relative-live-anchor";
                return false;
            };
            double residual = GhostRenderTrace.ResolveAttitudeResidualDegrees(
                new MockTrajectory { RecordingId = "r" }, null, 10.0,
                GhostRenderTrace.RenderSurface.Legacy, rendered: true,
                renderedRotation: Quaternion.identity, out string rotRef);
            Assert.True(double.IsNaN(residual));
            Assert.Equal("relative-live-anchor", rotRef);
        }

        [Fact]
        public void ResolveResidual_ResolverAnswers_MeasuresRenderedAgainstExpected()
        {
            Quaternion rendered = AxisAngle(33.0, 0.0, 1.0, 0.0);
            Quaternion expected = Mul(rendered, AxisAngle(7.5, 1.0, 0.0, 0.0));
            double seenUT = double.NaN;
            GhostRenderTrace.RenderSurface seenSurface = GhostRenderTrace.RenderSurface.Unknown;
            GhostRenderTrace.ExpectedAttitudeSource = (IPlaybackTrajectory t, GhostPlaybackState s, double ut,
                GhostRenderTrace.RenderSurface surf, out Quaternion e, out string r) =>
            {
                seenUT = ut;
                seenSurface = surf;
                e = expected;
                r = "checkpoint-orbit-ofr";
                return true;
            };
            double residual = GhostRenderTrace.ResolveAttitudeResidualDegrees(
                new MockTrajectory { RecordingId = "r" }, null, 1234.5,
                GhostRenderTrace.RenderSurface.BodyFixedPrimary, rendered: true,
                renderedRotation: rendered, out string rotRef);
            Assert.Equal(7.5, residual, 3);
            Assert.Equal("checkpoint-orbit-ofr", rotRef);
            Assert.Equal(1234.5, seenUT);
            Assert.Equal(GhostRenderTrace.RenderSurface.BodyFixedPrimary, seenSurface);
        }

        [Fact]
        public void EmitPostUpdate_NoGhost_AppendsNaNResidualAsLastFields()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            bool called = false;
            GhostRenderTrace.ExpectedAttitudeSource = (IPlaybackTrajectory t, GhostPlaybackState s, double ut,
                GhostRenderTrace.RenderSurface surf, out Quaternion e, out string r) =>
            {
                called = true;
                e = Quaternion.identity;
                r = "surface";
                return true;
            };
            GhostRenderTrace.EmitPostUpdate(
                trajectory: new MockTrajectory { RecordingId = "rec-attitude" }, ghostIndex: 0,
                currentUT: 50.0, playbackUT: 50.0,
                playbackState: null,
                path: "non-loop", retired: false);

            string line = Assert.Single(logLines.FindAll(l => l.Contains("phase=AfterUpdate")));
            Assert.EndsWith("clampFired=false dRotDeg=NaN rotRef=not-rendered", line);
            Assert.False(called);
        }

        [Fact]
        public void EmitPostUpdate_TracingOff_NeverCallsResolver()
        {
            bool called = false;
            GhostRenderTrace.ExpectedAttitudeSource = (IPlaybackTrajectory t, GhostPlaybackState s, double ut,
                GhostRenderTrace.RenderSurface surf, out Quaternion e, out string r) =>
            {
                called = true;
                e = Quaternion.identity;
                r = "surface";
                return true;
            };
            GhostRenderTrace.EmitPostUpdate(
                trajectory: new MockTrajectory { RecordingId = "rec-off" }, ghostIndex: 0,
                currentUT: 50.0, playbackUT: 50.0,
                playbackState: null,
                path: "non-loop", retired: false);
            Assert.False(called);
            Assert.DoesNotContain(logLines, l => l.Contains("phase=AfterUpdate"));
        }
    }
}
