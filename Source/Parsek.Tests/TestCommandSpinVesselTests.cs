using System.Globalization;
using System.Threading;
using Parsek.TestCommands;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>The pure half of the automation-only SpinVessel seam verb.</summary>
    public class TestCommandSpinVesselTests
    {
        [Theory]
        [InlineData("0.5", 0.5f)]
        [InlineData("2", 2f)]
        [InlineData("0.0001", 0.0001f)]
        public void ValidateRate_AcceptsInRange(string raw, float expected)
        {
            Assert.Null(TestCommandSpinVessel.ValidateRate(raw, out float rate));
            Assert.Equal((double)(expected), (double)(rate), 5);
        }

        [Theory]
        [InlineData(null, "spinvessel-rate-arg-missing")]
        [InlineData("", "spinvessel-rate-arg-missing")]
        [InlineData("0", "spinvessel-rate-arg-invalid")]
        [InlineData("-0.5", "spinvessel-rate-arg-invalid")]
        [InlineData("2.5", "spinvessel-rate-arg-invalid")]
        [InlineData("NaN", "spinvessel-rate-arg-invalid")]
        [InlineData("Infinity", "spinvessel-rate-arg-invalid")]
        [InlineData("fast", "spinvessel-rate-arg-invalid")]
        public void ValidateRate_RefusesOutOfRangeOrMalformed(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandSpinVessel.ValidateRate(raw, out float rate));
            Assert.Equal(0f, rate);
        }

        [Fact]
        public void ValidateRate_IsInvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Null(TestCommandSpinVessel.ValidateRate("0.5", out float rate));
                Assert.Equal((double)(0.5f), (double)(rate), 5);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void EveryRefusalReasonIsPrefixed()
        {
            foreach (string reason in TestCommandSpinVessel.Reasons)
                Assert.StartsWith("spinvessel-", reason);
        }

        [Fact]
        public void RollAxis_IsTheReferenceUp()
        {
            Quaternion reference = TrajectoryMath.PureAngleAxis(90f, Vector3.forward);
            Vector3 omega = TestCommandSpinVessel.ComputeRollAngularVelocityWorld(reference, 0.5f);
            // Local up rotated 90 deg about +Z points along -X.
            Assert.Equal((double)(-0.5f), (double)(omega.x), 4);
            Assert.Equal((double)(0f), (double)(omega.y), 4);
            Assert.Equal((double)(0f), (double)(omega.z), 4);
        }

        [Fact]
        public void TangentialVelocity_IsOmegaCrossOffset()
        {
            Vector3 v = TestCommandSpinVessel.ComputeTangentialVelocity(
                new Vector3(0f, 1f, 0f), new Vector3(2f, 5f, 0f), new Vector3(0f, 5f, 0f));
            // (0,1,0) x (2,0,0) = (0,0,-2).
            Assert.Equal((double)(0f), (double)(v.x), 4);
            Assert.Equal((double)(0f), (double)(v.y), 4);
            Assert.Equal((double)(-2f), (double)(v.z), 4);
            Assert.Equal(Vector3.zero, TestCommandSpinVessel.ComputeTangentialVelocity(
                new Vector3(0f, 1f, 0f), new Vector3(0f, 7f, 0f), new Vector3(0f, 5f, 0f)));
        }

        [Fact]
        public void AppliedLine_IsInvariantAndGrepStable()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(
                    "spinvessel applied: vessel=Kerbal X pid=42 rate=0.5000 axis=roll parts=7 sasWasOn=true",
                    TestCommandSpinVessel.FormatAppliedLine("Kerbal X", 42u, 0.5f, 7, true));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
