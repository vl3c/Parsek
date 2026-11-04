using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for small pure helpers of <see cref="FlightRecorder"/> that no
    /// test called directly: the deploy / retract event-name keyword classifiers, the
    /// aero-surface zero-angle veto, the wheel robotic module-name set, the stable UT sort,
    /// the plausible-rate formatter and the Re-Fly frame-offset application guards.
    /// </summary>
    public class FlightRecorderCharacterizationTests
    {
        // ---------- ClassifyAnimateGenericEventName ------------------------------

        [Theory]
        [InlineData("deploy", "", true, false)]
        [InlineData("", "extend legs", true, false)]
        [InlineData("openhatch", "", true, false)]
        [InlineData("", "inflate", true, false)]
        [InlineData("retract", "", false, true)]
        [InlineData("", "close door", false, true)]
        [InlineData("deflate", "", false, true)]
        [InlineData("toggle", "open/close", true, true)]
        [InlineData("toggle", "toggle", false, false)]
        [InlineData(null, null, false, false)]
        public void ClassifyAnimateGenericEventName_KeywordsInEitherName(
            string evtName, string guiName, bool expectDeploy, bool expectRetract)
        {
            FlightRecorder.ClassifyAnimateGenericEventName(
                evtName, guiName, out bool isDeploy, out bool isRetract);
            Assert.Equal(expectDeploy, isDeploy);
            Assert.Equal(expectRetract, isRetract);
        }

        [Fact]
        public void ClassifyAnimateGenericEventName_IsCaseSensitive()
        {
            // Callers lowercase first; an upper-case keyword is not recognized.
            FlightRecorder.ClassifyAnimateGenericEventName(
                "Deploy", "RETRACT", out bool isDeploy, out bool isRetract);
            Assert.False(isDeploy);
            Assert.False(isRetract);
        }

        // ---------- ClassifyAnimationGroupEventName ------------------------------

        [Theory]
        [InlineData("deployarm", "", true, false)]
        [InlineData("", "extend", true, false)]
        [InlineData("retract", "", false, true)]
        // AnimationGroup has a narrower vocabulary than AnimateGeneric.
        [InlineData("open", "close", false, false)]
        [InlineData("inflate", "deflate", false, false)]
        [InlineData(null, "deploy/retract", true, true)]
        public void ClassifyAnimationGroupEventName_DeployExtendRetractOnly(
            string evtName, string guiName, bool expectDeploy, bool expectRetract)
        {
            FlightRecorder.ClassifyAnimationGroupEventName(
                evtName, guiName, out bool isDeploy, out bool isRetract);
            Assert.Equal(expectDeploy, isDeploy);
            Assert.Equal(expectRetract, isRetract);
        }

        // ---------- ClassifyRetractableLadderEventName ---------------------------

        [Theory]
        [InlineData("extend", "", true, false)]
        [InlineData("", "retract ladder", false, true)]
        [InlineData("deploy", "open", false, false)]
        [InlineData(null, null, false, false)]
        public void ClassifyRetractableLadderEventName_ExtendRetractOnly(
            string evtName, string guiName, bool expectExtend, bool expectRetract)
        {
            FlightRecorder.ClassifyRetractableLadderEventName(
                evtName, guiName, out bool isExtend, out bool isRetract);
            Assert.Equal(expectExtend, isExtend);
            Assert.Equal(expectRetract, isRetract);
        }

        // ---------- AeroSurfaceDeployAngleIsInvisible ----------------------------

        [Fact]
        public void AeroSurfaceDeployAngleIsInvisible_NoFieldOrNonFinite_False()
        {
            Assert.False(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(new FakeModuleFieldValues()));
            Assert.False(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues().WithFloat("deployAngle", float.NaN)));
            Assert.False(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues().WithFloat("aeroDeployAngle", float.PositiveInfinity)));
        }

        [Fact]
        public void AeroSurfaceDeployAngleIsInvisible_StrictlyBelowHalfDegreeEitherSign()
        {
            Assert.True(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues().WithFloat("deployAngle", 0f)));
            Assert.True(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues().WithFloat("deployAngle", -0.49f)));
            Assert.False(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues().WithFloat("deployAngle",
                    FlightRecorder.AeroSurfaceVisibleDeployAngleDegrees)));
            Assert.False(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues().WithFloat("deployAngle", 15f)));
        }

        [Fact]
        public void AeroSurfaceDeployAngleIsInvisible_FirstFiniteFieldWins()
        {
            // aeroDeployAngle is probed first; a non-finite value falls through to deployAngle.
            Assert.False(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues()
                    .WithFloat("aeroDeployAngle", 20f)
                    .WithFloat("deployAngle", 0f)));
            Assert.True(FlightRecorder.AeroSurfaceDeployAngleIsInvisible(
                new FakeModuleFieldValues()
                    .WithFloat("aeroDeployAngle", float.NaN)
                    .WithFloat("deployAngle", 0f)));
        }

        // ---------- IsWheelRoboticModuleName ------------------------------------

        [Theory]
        [InlineData("ModuleWheelSuspension", true)]
        [InlineData("ModuleWheelSteering", true)]
        [InlineData("ModuleWheelMotor", true)]
        [InlineData("ModuleWheelMotorSteering", true)]
        [InlineData("ModuleWheelBase", false)]
        [InlineData("modulewheelmotor", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsWheelRoboticModuleName_ExactOrdinalSet(string name, bool expected)
        {
            Assert.Equal(expected, FlightRecorder.IsWheelRoboticModuleName(name));
        }

        // ---------- StableSortByUT ----------------------------------------------

        [Fact]
        public void StableSortByUT_NullAndShortListsReturnNewLists()
        {
            Assert.Empty(FlightRecorder.StableSortByUT<double>(null, x => x));
            var single = new List<double> { 5.0 };
            var copy = FlightRecorder.StableSortByUT(single, x => x);
            Assert.NotSame(single, copy);
            Assert.Equal(single, copy);
        }

        [Fact]
        public void StableSortByUT_SortsByUtKeepingSameUtInsertionOrder()
        {
            var input = new List<(double ut, string tag)>
            {
                (30.0, "c"), (10.0, "a1"), (20.0, "b"), (10.0, "a2"), (10.0, "a3")
            };
            var sorted = FlightRecorder.StableSortByUT(input, e => e.ut);
            Assert.NotSame(input, sorted);
            Assert.Equal(new[] { "a1", "a2", "a3", "b", "c" }, sorted.ConvertAll(e => e.tag));
            // The input list is untouched.
            Assert.Equal("c", input[0].tag);
        }

        // ---------- FormatPlausibleRates -----------------------------------------

        [Fact]
        public void FormatPlausibleRates_NullEmptyAndOrdinalSortedRoundTrip()
        {
            Assert.Equal("unbounded", FlightRecorder.FormatPlausibleRates(null));
            Assert.Equal("[]", FlightRecorder.FormatPlausibleRates(new Dictionary<string, double>()));
            Assert.Equal("[LiquidFuel:0.5,Ore:1.25,ore:3]",
                FlightRecorder.FormatPlausibleRates(new Dictionary<string, double>
                {
                    ["ore"] = 3.0,
                    ["Ore"] = 1.25,
                    ["LiquidFuel"] = 0.5
                }));
        }

        // ---------- TryApplyReFlyRecordingFrameOffsetToWorld ---------------------

        private static void AssertSame3(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x);
            Assert.Equal(expected.y, actual.y);
            Assert.Equal(expected.z, actual.z);
        }

        [Fact]
        public void TryApplyReFlyRecordingFrameOffsetToWorld_MissingIdOrNonFiniteRaw_NamedReasons()
        {
            var raw = new Vector3d(1.0, 2.0, 3.0);
            Assert.False(FlightRecorder.TryApplyReFlyRecordingFrameOffsetToWorld(
                null, 10.0, raw, out Vector3d canonical, out Vector3d offset, out string reason));
            Assert.Equal("recording-id-missing", reason);
            AssertSame3(raw, canonical);
            AssertSame3(Vector3d.zero, offset);

            Assert.False(FlightRecorder.TryApplyReFlyRecordingFrameOffsetToWorld(
                "rec_a", 10.0, new Vector3d(double.NaN, 0.0, 0.0), out _, out _, out reason));
            Assert.Equal("raw-world-non-finite", reason);
        }

        [Fact]
        public void TryApplyReFlyRecordingFrameOffsetToWorld_ValidInput_RefusedBecauseAlignmentRemoved()
        {
            // The offset resolver is retired, so every well-formed request is refused with
            // the raw position passed through unchanged.
            var raw = new Vector3d(100.0, -50.0, 25.0);
            Assert.False(FlightRecorder.TryApplyReFlyRecordingFrameOffsetToWorld(
                "rec_a", 10.0, raw, out Vector3d canonical, out Vector3d offset, out string reason));
            Assert.Equal("display-alignment-removed", reason);
            AssertSame3(raw, canonical);
            AssertSame3(Vector3d.zero, offset);
        }
    }
}
