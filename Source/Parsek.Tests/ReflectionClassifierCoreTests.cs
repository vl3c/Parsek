using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Dictionary-backed stand-in for one PartModule's readable state. Lets the pure
    /// interpretation cores of the reflection-classifier family run headlessly: a field that was
    /// never registered reads as "missing" exactly like an absent KSP field.
    /// Also records which names were probed so tests can pin the fallback ORDER and the laziness
    /// of the probe (a stage that already decided must not read the later stages).
    /// </summary>
    internal sealed class FakeModuleFieldValues : IModuleFieldValues
    {
        private readonly Dictionary<string, bool> bools = new Dictionary<string, bool>();
        private readonly Dictionary<string, float> floats = new Dictionary<string, float>();
        private readonly Dictionary<string, string> strings = new Dictionary<string, string>();
        private readonly Dictionary<string, float> vectorMagnitudes = new Dictionary<string, float>();
        private readonly Dictionary<string, float> rotationAngles = new Dictionary<string, float>();

        private bool hasScalarModuleScalar;
        private float scalarModuleScalar;

        private bool hasServoTransformAngle;
        private float servoTransformAngle;

        private bool hasServoRateOfMotion;
        private float servoRateOfMotionRpm;

        private bool sawDeployEvent;
        private bool sawRetractEvent;
        private bool canDeploy;
        private bool canRetract;

        internal readonly List<string> ProbedFieldNames = new List<string>();
        internal int EventActivityReadCount;
        internal int ScalarModuleReadCount;
        internal DeployEventKeywordSet? RequestedKeywordSet;

        internal FakeModuleFieldValues WithBool(string fieldName, bool value)
        {
            bools[fieldName] = value;
            return this;
        }

        internal FakeModuleFieldValues WithFloat(string fieldName, float value)
        {
            floats[fieldName] = value;
            return this;
        }

        internal FakeModuleFieldValues WithString(string fieldName, string value)
        {
            strings[fieldName] = value;
            return this;
        }

        internal FakeModuleFieldValues WithVectorMagnitude(string fieldName, float magnitude)
        {
            vectorMagnitudes[fieldName] = magnitude;
            return this;
        }

        internal FakeModuleFieldValues WithRotationAngle(string fieldName, float angleDegrees)
        {
            rotationAngles[fieldName] = angleDegrees;
            return this;
        }

        /// <summary>
        /// Registers the module as implementing <c>IScalarModule</c> with the given
        /// <c>GetScalar</c>. A fake that never calls this reads as "does not implement the
        /// interface", exactly like a PartModule that does not.
        /// </summary>
        internal FakeModuleFieldValues WithScalarModuleScalar(float value)
        {
            hasScalarModuleScalar = true;
            scalarModuleScalar = value;
            return this;
        }

        /// <summary>
        /// Registers the module as a Breaking Ground servo whose moving transform currently sits
        /// at the given main-axis angle. A fake that never calls this reads as "not a servo".
        /// </summary>
        internal FakeModuleFieldValues WithServoTransformAngle(float angleDegrees)
        {
            hasServoTransformAngle = true;
            servoTransformAngle = angleDegrees;
            return this;
        }

        internal int ServoTransformAngleReadCount;

        /// <summary>
        /// Registers the module as a Breaking Ground servo whose live (per-physics-frame) rate of
        /// motion currently reads the given RPM. A fake that never calls this has no live rate.
        /// </summary>
        internal FakeModuleFieldValues WithServoRateOfMotionRpm(float rpm)
        {
            hasServoRateOfMotion = true;
            servoRateOfMotionRpm = rpm;
            return this;
        }

        internal int ServoRateOfMotionReadCount;

        /// <summary>Registers the aggregated deploy/retract event availability of the module.</summary>
        internal FakeModuleFieldValues WithEventActivity(
            bool sawDeploy, bool sawRetract, bool deployAvailable, bool retractAvailable)
        {
            sawDeployEvent = sawDeploy;
            sawRetractEvent = sawRetract;
            canDeploy = deployAvailable;
            canRetract = retractAvailable;
            return this;
        }

        /// <summary>Shorthand: the module exposes both actions and only retract is available.</summary>
        internal FakeModuleFieldValues WithRetractAvailableOnly()
        {
            return WithEventActivity(true, true, false, true);
        }

        /// <summary>Shorthand: the module exposes both actions and only deploy is available.</summary>
        internal FakeModuleFieldValues WithDeployAvailableOnly()
        {
            return WithEventActivity(true, true, true, false);
        }

        public bool TryGetBool(string fieldName, out bool value)
        {
            ProbedFieldNames.Add(fieldName);
            return bools.TryGetValue(fieldName, out value);
        }

        public bool TryGetFloat(string fieldName, out float value)
        {
            ProbedFieldNames.Add(fieldName);
            return floats.TryGetValue(fieldName, out value);
        }

        public bool TryGetString(string fieldName, out string value)
        {
            ProbedFieldNames.Add(fieldName);
            return strings.TryGetValue(fieldName, out value);
        }

        public bool TryGetVectorMagnitude(string fieldName, out float magnitude)
        {
            ProbedFieldNames.Add(fieldName);
            return vectorMagnitudes.TryGetValue(fieldName, out magnitude);
        }

        public bool TryGetRotationAngleDegrees(string fieldName, out float angleDegrees)
        {
            ProbedFieldNames.Add(fieldName);
            return rotationAngles.TryGetValue(fieldName, out angleDegrees);
        }

        public bool TryGetScalarModuleScalar(out float scalar)
        {
            ScalarModuleReadCount++;
            scalar = scalarModuleScalar;
            return hasScalarModuleScalar;
        }

        public bool TryGetServoTransformAngleDegrees(out float angleDegrees)
        {
            ServoTransformAngleReadCount++;
            angleDegrees = servoTransformAngle;
            return hasServoTransformAngle;
        }

        public bool TryGetServoRateOfMotionRpm(out float rpm)
        {
            ServoRateOfMotionReadCount++;
            rpm = servoRateOfMotionRpm;
            return hasServoRateOfMotion;
        }

        public void ReadDeployRetractEventActivity(
            DeployEventKeywordSet keywordSet,
            out bool sawDeploy, out bool sawRetract, out bool deployAvailable, out bool retractAvailable)
        {
            EventActivityReadCount++;
            RequestedKeywordSet = keywordSet;
            sawDeploy = sawDeployEvent;
            sawRetract = sawRetractEvent;
            deployAvailable = canDeploy;
            retractAvailable = canRetract;
        }
    }

    /// <summary>
    /// Table tests over the pure interpretation halves of the reflection-classifier family in
    /// FlightRecorder (ladder / animation group / animate generic / aero + control surface /
    /// robot arm + scanner / animate heat / robotic moving / wheel robotic position / robotic
    /// position). The reflection half itself needs a live PartModule and is not covered here.
    /// </summary>
    public class ReflectionClassifierCoreTests
    {
        #region Event-keyword vocabularies

        [Theory]
        // Ladder vocabulary only knows extend/retract - "deploy" is not an extend keyword there.
        [InlineData("RetractableLadder", "extendladder", false, true, false)]
        [InlineData("RetractableLadder", "retractladder", false, false, true)]
        [InlineData("RetractableLadder", "deploy", false, false, false)]
        // Animation group adds deploy, but not open/close.
        [InlineData("AnimationGroup", "deploy", false, true, false)]
        [InlineData("AnimationGroup", "extend", false, true, false)]
        [InlineData("AnimationGroup", "retract", false, false, true)]
        [InlineData("AnimationGroup", "open bay", false, false, false)]
        // Animate generic adds open/close and inflate/deflate.
        [InlineData("AnimateGeneric", "open bay", false, true, false)]
        [InlineData("AnimateGeneric", "close bay", false, false, true)]
        [InlineData("AnimateGeneric", "inflate", false, true, false)]
        [InlineData("AnimateGeneric", "deflate", false, false, true)]
        // Aero vocabulary adds brake/enable/disable.
        [InlineData("AeroSurface", "brakes on", false, true, false)]
        [InlineData("AeroSurface", "disable steering", false, false, true)]
        // Robot arm / scanner vocabulary.
        [InlineData("RobotArmScanner", "start scan", false, true, false)]
        [InlineData("RobotArmScanner", "stop scan", false, true, true)]
        [InlineData("RobotArmScanner", "stow arm", false, false, true)]
        // Gui name is matched exactly like the event name.
        [InlineData("AnimateGeneric", "open cargo bay", true, true, false)]
        [InlineData("AeroSurface", "retract airbrake", true, true, true)]
        public void ClassifyDeployRetractEventName_DispatchesToTheRightVocabulary(
            string keywordSetName, string evtName, bool useGuiName,
            bool expectDeploy, bool expectRetract)
        {
            var keywordSet = (DeployEventKeywordSet)System.Enum.Parse(
                typeof(DeployEventKeywordSet), keywordSetName);
            string probeEvtName = useGuiName ? string.Empty : evtName;
            string probeGuiName = useGuiName ? evtName : string.Empty;

            FlightRecorder.ClassifyDeployRetractEventName(
                keywordSet, probeEvtName, probeGuiName, out bool isDeploy, out bool isRetract);

            Assert.Equal(expectDeploy, isDeploy);
            Assert.Equal(expectRetract, isRetract);
        }

        [Theory]
        [InlineData("unpackarm", true, false)]
        [InlineData("packarm", false, true)]
        [InlineData("deployarm", true, false)]
        [InlineData("cancel", false, true)]
        // Overlapping keywords legitimately set both: the caller records that the module exposes
        // both actions and lets the availability flags decide.
        [InlineData("cancelscan", true, true)]
        public void ClassifyRobotArmScannerEventName_UnpackIsNotAPack(
            string evtName, bool expectDeploy, bool expectRetract)
        {
            FlightRecorder.ClassifyRobotArmScannerEventName(
                evtName, string.Empty, out bool isDeploy, out bool isRetract);

            Assert.Equal(expectDeploy, isDeploy);
            Assert.Equal(expectRetract, isRetract);
        }

        [Fact]
        public void ClassifyRobotArmScannerEventName_UnpackInGuiNameAlsoSuppressesThePackMatch()
        {
            FlightRecorder.ClassifyRobotArmScannerEventName(
                string.Empty, "unpack arm", out bool isDeploy, out bool isRetract);

            Assert.True(isDeploy);
            Assert.False(isRetract);
        }

        #endregion

        #region Retractable ladder core

        [Theory]
        [InlineData("Extended", true, false)]
        [InlineData("Deployed", true, false)]
        [InlineData("Retracted", false, true)]
        [InlineData("Stowed", false, true)]
        public void LadderCore_StateNameWinsOverEverythingElse(
            string stateName, bool expectDeployed, bool expectRetracted)
        {
            // Event activity and bool fields all disagree with StateName on purpose.
            var fields = new FakeModuleFieldValues()
                .WithString(FlightRecorder.RetractableLadderStateFieldName, stateName)
                .WithEventActivity(true, true, !expectRetracted, !expectDeployed)
                .WithBool("isDeployed", !expectDeployed);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
            Assert.Equal(0, fields.EventActivityReadCount);
            Assert.DoesNotContain("isDeployed", fields.ProbedFieldNames);
        }

        [Theory]
        [InlineData("Extending")]
        [InlineData("Retracting")]
        public void LadderCore_TransientStateNameAbortsWithoutConsultingFallbacks(string stateName)
        {
            var fields = new FakeModuleFieldValues()
                .WithString(FlightRecorder.RetractableLadderStateFieldName, stateName)
                .WithRetractAvailableOnly()
                .WithBool("isDeployed", true);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
            Assert.Equal(0, fields.EventActivityReadCount);
            Assert.DoesNotContain("isDeployed", fields.ProbedFieldNames);
        }

        [Fact]
        public void LadderCore_UnknownStateNameFallsThroughToEventActivity()
        {
            var fields = new FakeModuleFieldValues()
                .WithString(FlightRecorder.RetractableLadderStateFieldName, "Wibbling")
                .WithRetractAvailableOnly();

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
            Assert.Equal(DeployEventKeywordSet.RetractableLadder, fields.RequestedKeywordSet);
        }

        [Fact]
        public void LadderCore_RetractAvailableMeansDeployed()
        {
            var fields = new FakeModuleFieldValues().WithRetractAvailableOnly();

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void LadderCore_ExtendAvailableMeansRetracted()
        {
            var fields = new FakeModuleFieldValues().WithDeployAvailableOnly();

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void LadderCore_BothActionsAvailableIsAmbiguousAndFallsThroughToBoolFields()
        {
            var fields = new FakeModuleFieldValues()
                .WithEventActivity(true, true, true, true)
                .WithBool("extended", true);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void LadderCore_NoMatchingEventsSkipsTheActivityStageEvenWhenAvailabilityLooksDecisive()
        {
            // sawDeploy/sawRetract false: the module has no extend/retract events at all, so the
            // availability flags are meaningless and must not classify anything.
            var fields = new FakeModuleFieldValues()
                .WithEventActivity(false, false, false, true)
                .WithBool("isRetracted", true);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Theory]
        [InlineData("isDeployed", true, true, false)]
        [InlineData("deployed", false, false, true)]
        [InlineData("isExtended", true, true, false)]
        [InlineData("extended", false, false, true)]
        public void LadderCore_DeployedFamilyBoolFieldsMapDirectly(
            string fieldName, bool fieldValue, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, fieldValue);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Theory]
        [InlineData("isRetracted", true, false, true)]
        [InlineData("retracted", false, true, false)]
        public void LadderCore_RetractedFamilyBoolFieldsAreInverted(
            string fieldName, bool fieldValue, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, fieldValue);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Fact]
        public void LadderCore_DeployedFamilyOutranksRetractedFamilyWhenBothArePresent()
        {
            // A module carrying both flags out of sync: the deployed family is authoritative.
            var fields = new FakeModuleFieldValues()
                .WithBool("deployed", true)
                .WithBool("isRetracted", true);

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void LadderCore_NoStateNameNoEventsNoFieldsIsUnclassified()
        {
            var fields = new FakeModuleFieldValues();

            bool ok = FlightRecorder.TryClassifyRetractableLadderStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        #endregion

        #region Animation group core

        [Fact]
        public void AnimationGroupCore_UsesTheAnimationGroupVocabularyAndPrefersEventActivity()
        {
            var fields = new FakeModuleFieldValues()
                .WithRetractAvailableOnly()
                .WithBool("isRetracted", true);

            bool ok = FlightRecorder.TryClassifyAnimationGroupStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
            Assert.Equal(DeployEventKeywordSet.AnimationGroup, fields.RequestedKeywordSet);
        }

        [Fact]
        public void AnimationGroupCore_FallsBackToBoolFieldsWhenActivityIsAmbiguous()
        {
            var fields = new FakeModuleFieldValues()
                .WithEventActivity(true, true, true, true)
                .WithBool("isDeployed", false);

            bool ok = FlightRecorder.TryClassifyAnimationGroupStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void AnimationGroupCore_UnknownModuleIsUnclassified()
        {
            var fields = new FakeModuleFieldValues();

            bool ok = FlightRecorder.TryClassifyAnimationGroupStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        #endregion

        #region Animate generic core

        [Theory]
        [InlineData(1f, true, false)]
        [InlineData(0.99f, true, false)]
        [InlineData(0f, false, true)]
        [InlineData(0.01f, false, true)]
        public void AnimateGenericCore_AnimTimeEndpointsDecideWithoutReadingAnythingElse(
            float animTime, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues()
                .WithEventActivity(true, true, !expectRetracted, !expectDeployed)
                .WithBool("isDeployed", !expectDeployed);

            bool ok = FlightRecorder.TryClassifyAnimateGenericStateFromFieldValues(
                fields, animTime, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
            Assert.Equal(0, fields.EventActivityReadCount);
            Assert.Empty(fields.ProbedFieldNames);
        }

        [Theory]
        [InlineData(0.5f)]
        [InlineData(0.02f)]
        [InlineData(0.98f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void AnimateGenericCore_MidTravelOrNonFiniteAnimTimeFallsThroughToEventActivity(
            float animTime)
        {
            var fields = new FakeModuleFieldValues().WithRetractAvailableOnly();

            bool ok = FlightRecorder.TryClassifyAnimateGenericStateFromFieldValues(
                fields, animTime, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
            Assert.Equal(DeployEventKeywordSet.AnimateGeneric, fields.RequestedKeywordSet);
        }

        [Fact]
        public void AnimateGenericCore_MidTravelWithNoOtherSignalIsUnclassified()
        {
            var fields = new FakeModuleFieldValues();

            bool ok = FlightRecorder.TryClassifyAnimateGenericStateFromFieldValues(
                fields, 0.5f, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void AnimateGenericCore_MidTravelFallsAllTheWayThroughToBoolFields()
        {
            var fields = new FakeModuleFieldValues().WithBool("extended", true);

            bool ok = FlightRecorder.TryClassifyAnimateGenericStateFromFieldValues(
                fields, 0.5f, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        #endregion

        #region Aero / control surface core

        [Fact]
        public void AeroCore_EventActivityWinsOverFieldValues()
        {
            var fields = new FakeModuleFieldValues()
                .WithRetractAvailableOnly()
                .WithBool("isDeployed", false)
                .WithFloat("currentDeflection", 0f);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
            Assert.Equal(DeployEventKeywordSet.AeroSurface, fields.RequestedKeywordSet);
        }

        [Theory]
        [InlineData("isBraking", true, true, false)]
        [InlineData("brakesOn", false, false, true)]
        [InlineData("isActivated", true, true, false)]
        [InlineData("active", true, true, false)]
        public void AeroCore_AeroSpecificDeployedFieldsAreProbed(
            string fieldName, bool fieldValue, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, fieldValue);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Theory]
        [InlineData("isStowed", true, false, true)]
        [InlineData("stowed", false, true, false)]
        [InlineData("isPacked", true, false, true)]
        [InlineData("packed", true, false, true)]
        public void AeroCore_AeroSpecificRetractedFieldsAreInverted(
            string fieldName, bool fieldValue, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, fieldValue);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Theory]
        [InlineData(0.5f, true)]
        [InlineData(-0.5f, true)]      // deflection sign is irrelevant, magnitude is what counts
        [InlineData(0.011f, true)]
        [InlineData(0.01f, false)]     // exactly at the threshold is NOT deflected
        [InlineData(0f, false)]
        [InlineData(-0.001f, false)]
        public void AeroCore_DeflectionMagnitudeDecidesWhenNoBoolFieldExists(
            float deflection, bool expectDeployed)
        {
            var fields = new FakeModuleFieldValues().WithFloat("currentDeflection", deflection);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(!expectDeployed, isRetracted);
        }

        [Fact]
        public void AeroCore_NonFiniteDeflectionIsSkippedNotReadAsZero()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentDeflection", float.NaN)
                .WithFloat("deflection", 0.8f);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void AeroCore_AllDeflectionCandidatesNonFiniteIsUnclassified()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentDeflection", float.NaN)
                .WithFloat("deflection", float.PositiveInfinity)
                .WithFloat("deployPercent", float.NegativeInfinity)
                .WithFloat("position", float.NaN);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void AeroCore_BoolFieldOutranksDeflection()
        {
            var fields = new FakeModuleFieldValues()
                .WithBool("isDeployed", false)
                .WithFloat("currentDeflection", 0.9f);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void AeroCore_LaterDeflectionCandidatesAreOnlyUsedWhenEarlierOnesAreAbsent()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("deployPercent", 0f)
                .WithFloat("position", 0.9f);

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void AeroCore_NothingReadableIsUnclassified()
        {
            var fields = new FakeModuleFieldValues();

            bool ok = FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        #endregion

        #region Robot arm / scanner core

        [Fact]
        public void RobotArmCore_EventActivityWinsAndUsesTheScannerVocabulary()
        {
            var fields = new FakeModuleFieldValues()
                .WithDeployAvailableOnly()
                .WithBool("isUnpacked", true);

            bool ok = FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
            Assert.Equal(DeployEventKeywordSet.RobotArmScanner, fields.RequestedKeywordSet);
        }

        [Theory]
        [InlineData("isUnpacked", true, true, false)]
        [InlineData("unpacked", false, false, true)]
        [InlineData("isScanning", true, true, false)]
        [InlineData("working", true, true, false)]
        public void RobotArmCore_ScannerSpecificDeployedFieldsAreProbed(
            string fieldName, bool fieldValue, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, fieldValue);

            bool ok = FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Theory]
        [InlineData("isPacked", true, false, true)]
        [InlineData("stowed", false, true, false)]
        public void RobotArmCore_ScannerSpecificRetractedFieldsAreInverted(
            string fieldName, bool fieldValue, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, fieldValue);

            bool ok = FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Theory]
        [InlineData(1f, true, false)]
        [InlineData(0f, false, true)]
        public void RobotArmCore_AnimTimeIsTheLastResort(
            float animTime, bool expectDeployed, bool expectRetracted)
        {
            var fields = new FakeModuleFieldValues().WithFloat("animTime", animTime);

            bool ok = FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.Equal(expectDeployed, isDeployed);
            Assert.Equal(expectRetracted, isRetracted);
        }

        [Theory]
        [InlineData(0.5f)]
        [InlineData(float.NaN)]
        [InlineData(float.NegativeInfinity)]
        public void RobotArmCore_MidTravelOrNonFiniteAnimTimeStaysUnclassified(float animTime)
        {
            var fields = new FakeModuleFieldValues().WithFloat("animTime", animTime);

            bool ok = FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.False(ok);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void RobotArmCore_BoolFieldsOutrankAnimTime()
        {
            var fields = new FakeModuleFieldValues()
                .WithBool("isPacked", true)
                .WithFloat("animTime", 1f);

            bool ok = FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted);

            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        #endregion

        #region Animate heat core

        [Fact]
        public void AnimateHeatCore_FirstCandidateFieldWinsAndIsReported()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("animTime", 0.4f)
                .WithFloat("heat", 0.9f);

            bool ok = FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField);

            Assert.True(ok);
            Assert.Equal("animTime", sourceField);
            Assert.Equal(0.4, normalizedHeat, 4);
        }

        [Fact]
        public void AnimateHeatCore_NonFiniteCandidateIsSkippedInFavourOfTheNextOne()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("animTime", float.NaN)
                .WithFloat("heatValue", 0.25f);

            bool ok = FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField);

            Assert.True(ok);
            Assert.Equal("heatValue", sourceField);
            Assert.Equal(0.25, normalizedHeat, 4);
        }

        [Theory]
        [InlineData(0.5f, 0.5)]      // 0..1 scalar passes through
        [InlineData(1.25f, 1.0)]     // above-1 animation overshoot clamps
        [InlineData(-0.25f, 0.0)]    // below-0 overshoot clamps
        [InlineData(50f, 0.5)]       // percent-like
        [InlineData(100f, 1.0)]
        [InlineData(900f, 0.5)]      // absolute temperature: (900-300)/1200
        [InlineData(300f, 0.0)]
        [InlineData(1500f, 1.0)]
        public void AnimateHeatCore_ScalarShapesAreNormalizedToZeroOne(float raw, double expected)
        {
            var fields = new FakeModuleFieldValues().WithFloat("animTime", raw);

            bool ok = FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField);

            Assert.True(ok);
            Assert.Equal("animTime", sourceField);
            Assert.Equal(expected, normalizedHeat, 4);
        }

        [Fact]
        public void AnimateHeatCore_NoCandidateFieldIsUnclassified()
        {
            var fields = new FakeModuleFieldValues().WithFloat("someOtherField", 0.5f);

            bool ok = FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField);

            Assert.False(ok);
            Assert.Null(sourceField);
            Assert.Equal(0f, normalizedHeat);
        }

        #endregion

        #region Robotic moving core

        [Fact]
        public void RoboticMovingCore_FirstResolvedFlagWinsEvenWhenItIsFalse()
        {
            // A module exposing both flags: the earlier one is authoritative, so a stale
            // "isMoving=true" must not override the servo's own "not moving" answer.
            var fields = new FakeModuleFieldValues()
                .WithBool("servoIsMoving", false)
                .WithBool("isMoving", true);

            bool ok = FlightRecorder.TryClassifyRoboticMovingFromFieldValues(fields, out bool moving);

            Assert.True(ok);
            Assert.False(moving);
        }

        [Theory]
        [InlineData("isMoving")]
        [InlineData("moving")]
        [InlineData("isTraversing")]
        [InlineData("isRotating")]
        public void RoboticMovingCore_AnyOfTheFallbackFlagsIsAccepted(string fieldName)
        {
            var fields = new FakeModuleFieldValues().WithBool(fieldName, true);

            bool ok = FlightRecorder.TryClassifyRoboticMovingFromFieldValues(fields, out bool moving);

            Assert.True(ok);
            Assert.True(moving);
        }

        [Fact]
        public void RoboticMovingCore_NoFlagAtAllReportsNoSignal()
        {
            var fields = new FakeModuleFieldValues().WithBool("somethingElse", true);

            bool ok = FlightRecorder.TryClassifyRoboticMovingFromFieldValues(fields, out bool moving);

            Assert.False(ok);
            Assert.False(moving);
        }

        #endregion

        #region Wheel robotic position core

        [Fact]
        public void WheelPlan_DeadbandsAreOrderedBySignalGranularity()
        {
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelSuspension", out float suspensionDeadband, out string[] suspensionFields);
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelSteering", out float steeringDeadband, out string[] steeringFields);
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelMotor", out float motorDeadband, out string[] motorFields);

            // Suspension travel (metres) needs a finer deadband than steering (degrees), which in
            // turn needs a finer one than motor RPM.
            Assert.True(suspensionDeadband < steeringDeadband);
            Assert.True(steeringDeadband < motorDeadband);
            Assert.True(suspensionDeadband > 0f);

            Assert.NotEmpty(suspensionFields);
            Assert.NotEmpty(steeringFields);
            Assert.NotEmpty(motorFields);
            Assert.NotEqual(suspensionFields[0], steeringFields[0]);
            Assert.NotEqual(steeringFields[0], motorFields[0]);
        }

        [Fact]
        public void WheelCore_SuspensionUsesItsOwnScalarPlanAndDeadband()
        {
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelSuspension", out float planDeadband, out string[] planFields);

            var fields = new FakeModuleFieldValues().WithFloat(planFields[2], 0.3f);

            bool ok = FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelSuspension",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal(planFields[2], sourceField);
            Assert.Equal(0.3, positionValue, 5);
            Assert.Equal(planDeadband, deadband);
        }

        [Fact]
        public void WheelCore_SuspensionFallsBackToTheSuspensionPositionVector()
        {
            var fields = new FakeModuleFieldValues().WithVectorMagnitude("suspensionPos", 0.42f);

            bool ok = FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelSuspension",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal("suspensionPos", sourceField);
            Assert.Equal(0.42, positionValue, 5);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void WheelCore_SuspensionPositionVectorIsNotConsultedForOtherWheelModules()
        {
            var fields = new FakeModuleFieldValues().WithVectorMagnitude("suspensionPos", 0.42f);

            bool ok = FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelSteering",
                out float positionValue, out float deadband, out string sourceField);

            Assert.False(ok);
            Assert.Null(sourceField);
            Assert.Equal(0f, positionValue);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void WheelCore_MotorSteeringFallsBackToSteeringAngleAndTightensTheDeadband()
        {
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelSteering", out float steeringPlanDeadband, out string[] _);
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelMotorSteering", out float motorPlanDeadband, out string[] _);

            var fields = new FakeModuleFieldValues().WithFloat("steeringAngle", 8f);

            bool ok = FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelMotorSteering",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal("steeringAngle", sourceField);
            Assert.Equal(8.0, positionValue, 5);
            // The RPM-shaped default deadband is replaced by the steering one.
            Assert.Equal(steeringPlanDeadband, deadband);
            Assert.True(deadband < motorPlanDeadband);
        }

        [Fact]
        public void WheelCore_MotorPrefersItsRpmPlanOverTheSteeringAngleFallback()
        {
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelMotorSteering", out float planDeadband, out string[] planFields);

            var fields = new FakeModuleFieldValues()
                .WithFloat(planFields[0], 120f)
                .WithFloat("steeringAngle", 8f);

            bool ok = FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelMotorSteering",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal(planFields[0], sourceField);
            Assert.Equal(120.0, positionValue, 3);
            Assert.Equal(planDeadband, deadband);
        }

        [Fact]
        public void WheelCore_UnreadableModuleReportsNoPositionButStillYieldsADeadband()
        {
            var fields = new FakeModuleFieldValues();

            bool ok = FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelMotor",
                out float positionValue, out float deadband, out string sourceField);

            Assert.False(ok);
            Assert.Null(sourceField);
            Assert.Equal(0f, positionValue);
            Assert.True(deadband > 0f);
        }

        #endregion

        #region Robotic position core

        [Fact]
        public void RoboticPlan_PistonIsLinearAndTheOthersAreAngular()
        {
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoPiston", out float pistonDeadband, out string[] pistonFields);
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoHinge", out float hingeDeadband, out string[] hingeFields);
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoRotor", out float rotorDeadband, out string[] rotorFields);

            // A piston's deadband is in metres, a hinge's is in degrees: they must not be the same
            // number, and the metric one is the tighter of the two.
            Assert.True(pistonDeadband < hingeDeadband);
            Assert.Equal(hingeDeadband, rotorDeadband);

            Assert.NotEqual(pistonFields[0], hingeFields[0]);
            Assert.NotEqual(rotorFields[0], hingeFields[0]);
        }

        [Fact]
        public void RoboticCore_HingeReadsItsAnglePlan()
        {
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoHinge", out float planDeadband, out string[] planFields);

            var fields = new FakeModuleFieldValues().WithFloat(planFields[0], 42f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoHinge",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal(planFields[0], sourceField);
            Assert.Equal(42.0, positionValue, 3);
            Assert.Equal(planDeadband, deadband);
        }

        [Fact]
        public void RoboticCore_RotorDoesNotReadTheHingeAnglePlan()
        {
            // currentAngle belongs to the hinge/rotation-servo plan; a rotor samples RPM instead.
            var fields = new FakeModuleFieldValues().WithFloat("currentAngle", 42f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoRotor",
                out float positionValue, out float deadband, out string sourceField);

            Assert.False(ok);
            Assert.Null(sourceField);
            Assert.Equal(0f, positionValue);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void RoboticCore_PistonReadsItsLinearPlan()
        {
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoPiston", out float planDeadband, out string[] planFields);

            var fields = new FakeModuleFieldValues().WithFloat(planFields[1], 0.75f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoPiston",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal(planFields[1], sourceField);
            Assert.Equal(0.75, positionValue, 5);
            Assert.Equal(planDeadband, deadband);
        }

        [Fact]
        public void RoboticCore_FallsBackToServoTransformPositionMagnitude()
        {
            var fields = new FakeModuleFieldValues()
                .WithVectorMagnitude("servoTransformPosition", 1.5f)
                .WithRotationAngle("servoTransformRotation", 30f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoHinge",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal("servoTransformPosition", sourceField);
            Assert.Equal(1.5, positionValue, 5);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void RoboticCore_FallsBackToServoTransformRotationAngleLast()
        {
            var fields = new FakeModuleFieldValues().WithRotationAngle("servoTransformRotation", 30f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticRotationServo",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal("servoTransformRotation", sourceField);
            Assert.Equal(30.0, positionValue, 3);
        }

        [Fact]
        public void RoboticCore_ScalarPlanOutranksTheTransformFallbacks()
        {
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticRotationServo", out float _, out string[] planFields);

            var fields = new FakeModuleFieldValues()
                .WithFloat(planFields[0], 12f)
                .WithVectorMagnitude("servoTransformPosition", 1.5f)
                .WithRotationAngle("servoTransformRotation", 30f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticRotationServo",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal(planFields[0], sourceField);
            Assert.Equal(12.0, positionValue, 3);
        }

        [Fact]
        public void RoboticCore_WheelModuleNamesRouteToTheWheelPlan()
        {
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoHinge", out float angularDeadband, out string[] _);
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelSteering", out float steeringDeadband, out string[] steeringFields);

            var fields = new FakeModuleFieldValues().WithFloat(steeringFields[0], 15f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleWheelSteering",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal(steeringFields[0], sourceField);
            Assert.Equal(15.0, positionValue, 3);
            Assert.Equal(steeringDeadband, deadband);
            Assert.NotEqual(angularDeadband, deadband);
        }

        // Stock refreshes a hinge's / rotation servo's `currentAngle` only in UpdatePAWUI
        // (OnStart, OnPartMenuOpen, Update while the menu is open; KSP 1.12.5 decompiled), so
        // with the part menu closed it holds its start value while the servo turns. The live
        // signal is the moving transform's main-axis angle (stock's currentTransformAngle()).
        // SRV-1's first flight (2026-11-20) tracked a hinge `source=currentAngle` through two
        // full swings and recorded zero robotic events.

        [Fact]
        public void RoboticCore_HingeReadsTheLiveTransformAngleOverTheStaleMenuField()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentAngle", 0f)
                .WithFloat("targetAngle", 90f)
                .WithServoTransformAngle(37.5f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoHinge",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal("servoTransformAngle", sourceField);
            Assert.Equal(37.5, positionValue, 3);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void RoboticCore_HingeLiveAngleAddsTheModelInitialAngleAsStockDisplaysIt()
        {
            // ModuleRoboticServoHinge.UpdatePAWUI: currentAngle = currentTransformAngle() +
            // modelInitialAngle, the same convention as targetAngle and the ghost's baseline.
            var fields = new FakeModuleFieldValues()
                .WithFloat("modelInitialAngle", 90f)
                .WithFloat("currentAngle", 90f)
                .WithServoTransformAngle(-30f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoHinge",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("servoTransformAngle", sourceField);
            Assert.Equal(60.0, positionValue, 3);
        }

        [Fact]
        public void RoboticCore_RotationServoReadsTheLiveTransformAngleWithNoOffset()
        {
            // ModuleRoboticRotationServo.UpdatePAWUI: currentAngle = currentTransformAngle().
            var fields = new FakeModuleFieldValues()
                .WithFloat("modelInitialAngle", 90f)
                .WithFloat("currentAngle", 0f)
                .WithServoTransformAngle(-120f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticRotationServo",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("servoTransformAngle", sourceField);
            Assert.Equal(-120.0, positionValue, 3);
        }

        [Theory]
        [InlineData("ModuleRoboticServoPiston")]
        [InlineData("ModuleRoboticServoRotor")]
        public void RoboticCore_PistonAndRotorDoNotReadTheTransformAngle(string moduleName)
        {
            // A piston's currentExtension is refreshed in OnFixedUpdate, and a rotor samples
            // RPM: neither is an angle, so the angular live probe must not be consulted.
            var fields = new FakeModuleFieldValues().WithServoTransformAngle(45f);

            FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, moduleName, out _, out _, out string sourceField);

            Assert.NotEqual("servoTransformAngle", sourceField);
            Assert.Equal(0, fields.ServoTransformAngleReadCount);
        }

        [Fact]
        public void RoboticCore_HingeWithoutALiveTransformFallsBackToItsFieldPlan()
        {
            var fields = new FakeModuleFieldValues().WithFloat("currentAngle", 12f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoHinge",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("currentAngle", sourceField);
            Assert.Equal(12.0, positionValue, 3);
        }

        [Theory]
        [InlineData("X", 10f, 20f, 30f, true, 10f)]
        [InlineData("Y", 10f, 20f, 30f, true, 20f)]
        [InlineData("Z", 10f, 20f, 30f, true, 30f)]
        [InlineData("X", 270f, 0f, 0f, true, -90f)]
        [InlineData("X", 180f, 0f, 0f, true, 180f)]
        [InlineData("X", 180.5f, 0f, 0f, true, -179.5f)]
        [InlineData("x", 10f, 0f, 0f, false, 0f)]
        [InlineData("", 10f, 0f, 0f, false, 0f)]
        [InlineData(null, 10f, 0f, 0f, false, 0f)]
        public void ServoMainAxisAngle_SelectsAndWrapsLikeStock(
            string axis, float x, float y, float z, bool expectOk, float expected)
        {
            bool ok = FlightRecorder.TrySelectServoMainAxisAngle(axis, x, y, z, out float angle);

            Assert.Equal(expectOk, ok);
            Assert.Equal((double)expected, (double)angle, 3);
        }

        [Fact]
        public void ServoMainAxisAngle_RefusesANonFiniteAngle()
        {
            Assert.False(FlightRecorder.TrySelectServoMainAxisAngle(
                "X", float.NaN, 0f, 0f, out float angle));
            Assert.Equal(0f, angle);
        }

        // Stock refreshes a rotor's `currentRPM` only in ModuleRoboticServoRotor.UpdatePAWUI
        // (`currentRPM = transformRateOfMotion`, every 0.3 s while the part menu is open; KSP
        // 1.12.5 decompiled). With the menu closed it keeps its start value (0) while the rotor
        // spins. The live signal is BaseServo.transformRateOfMotion, which FixedUpdate refreshes
        // every physics frame (CalculateAverageRateOfMovement, RPM averaged over 20 frames):
        // the same quantity and scale currentRPM copies, so the ghost's RPM spin is unchanged.

        [Fact]
        public void RoboticCore_RotorReadsTheLiveRateOfMotionOverTheStaleMenuField()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentRPM", 0f)
                .WithFloat("rpmLimit", 230f)
                .WithServoRateOfMotionRpm(118.25f);

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoRotor",
                out float positionValue, out float deadband, out string sourceField);

            Assert.True(ok);
            Assert.Equal("servoRateOfMotion", sourceField);
            Assert.Equal(118.25, positionValue, 3);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void RoboticCore_RotorAtRestReadsZeroFromTheLiveRateNotTheRpmLimit()
        {
            // A stopped rotor reads 0 RPM live; the plan's rpmLimit fallback (a setting, not a
            // motion) must not be reached while the live rate answers.
            var fields = new FakeModuleFieldValues()
                .WithFloat("rpmLimit", 230f)
                .WithServoRateOfMotionRpm(0f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoRotor",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("servoRateOfMotion", sourceField);
            Assert.Equal(0f, positionValue);
            Assert.DoesNotContain("rpmLimit", fields.ProbedFieldNames);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void RoboticCore_RotorWithANonFiniteLiveRateFallsBackToItsFieldPlan(float rate)
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentRPM", 12f)
                .WithServoRateOfMotionRpm(rate);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoRotor",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("currentRPM", sourceField);
            Assert.Equal(12.0, positionValue, 3);
        }

        [Fact]
        public void RoboticCore_RotorWithoutALiveRateFallsBackToItsFieldPlan()
        {
            var fields = new FakeModuleFieldValues().WithFloat("currentRPM", 12f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoRotor",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("currentRPM", sourceField);
            Assert.Equal(12.0, positionValue, 3);
        }

        // A rotor's recorded value is a SPEED, but the transition machine infers "moving" from
        // the value changing, so a rotor holding a steady RPM read as stopped and emitted
        // RoboticMotionStopped, which the ghost's RotorRpm mode treats as "stop spinning". For
        // a measured rotor RPM the moving signal is the spin itself: stock's
        // ModuleRoboticServoRotor.IsMoving() is `transformRateOfMotion > 0.5f`.

        [Theory]
        [InlineData("servoRateOfMotion", 230f, true)]
        [InlineData("servoRateOfMotion", 0.6f, true)]
        [InlineData("servoRateOfMotion", 0.5f, false)]
        [InlineData("servoRateOfMotion", 0f, false)]
        [InlineData("currentRPM", 120f, true)]
        [InlineData("currentRPM", 0f, false)]
        public void RoboticMovingSignal_RotorRpmIsSpinningAboveStocksThreshold(
            string sourceField, float rpm, bool expectMoving)
        {
            FlightRecorder.ResolveRoboticMovingSignal(
                "ModuleRoboticServoRotor", sourceField, rpm,
                hasModuleFlag: false, moduleFlag: false,
                out bool moving, out string movingSource);

            Assert.Equal(expectMoving, moving);
            Assert.Equal("rotor-rpm", movingSource);
        }

        [Fact]
        public void RoboticMovingSignal_RotorRpmLimitIsASettingNotASpin()
        {
            // rpmLimit is the slider, constant while the motor is off: never a spin signal.
            FlightRecorder.ResolveRoboticMovingSignal(
                "ModuleRoboticServoRotor", "rpmLimit", 230f,
                hasModuleFlag: false, moduleFlag: false,
                out bool moving, out string movingSource);

            Assert.False(moving);
            Assert.Equal("inferred", movingSource);
        }

        [Theory]
        [InlineData("ModuleRoboticServoHinge", "servoTransformAngle", false, false, false, "inferred")]
        [InlineData("ModuleRoboticRotationServo", "servoTransformAngle", true, true, true, "module-flag")]
        [InlineData("ModuleRoboticServoPiston", "currentExtension", true, false, false, "module-flag")]
        public void RoboticMovingSignal_PoseServosKeepTheirModuleFlagOrInference(
            string moduleName, string sourceField, bool hasFlag, bool flag,
            bool expectMoving, string expectSource)
        {
            FlightRecorder.ResolveRoboticMovingSignal(
                moduleName, sourceField, 200f, hasFlag, flag,
                out bool moving, out string movingSource);

            Assert.Equal(expectMoving, moving);
            Assert.Equal(expectSource, movingSource);
        }

        [Fact]
        public void RoboticTransition_ARotorHoldingASteadyRpmStaysMovingUntilItSpinsDown()
        {
            ulong key = FlightRecorder.EncodeEngineKey(400, 0);
            var movingSet = new HashSet<ulong>();
            var positions = new Dictionary<ulong, float>();
            var sampleUT = new Dictionary<ulong, double>();
            var windows = new Dictionary<ulong, FlightRecorder.RoboticMotionWindow>();
            var events = new List<PartEvent>();

            // 0.02 s physics frames: spin up 0 -> 230 RPM over 1 s, hold 3 s, spin down over 1 s,
            // then rest 1 s.
            double ut = 100.0;
            for (int frame = 0; frame <= 300; frame++, ut += 0.02)
            {
                float rpm;
                if (frame <= 50) rpm = 230f * frame / 50f;
                else if (frame <= 200) rpm = 230f;
                else if (frame <= 250) rpm = 230f * (250 - frame) / 50f;
                else rpm = 0f;

                events.AddRange(FlightRecorder.EvaluateRoboticPoll(
                    key, 400, 0, "rotor.01", "ModuleRoboticServoRotor", "servoRateOfMotion",
                    hasModuleFlag: false, moduleFlag: false, positionValue: rpm, deadband: 0.5f, ut: ut,
                    movingSet: movingSet, lastPositionMap: positions, lastSampleMap: sampleUT,
                    motionWindows: windows, sampleIntervalSeconds: 0.25,
                    movingSource: out _, sampleDeadband: out _));
            }

            Assert.Single(events, e => e.eventType == PartEventType.RoboticMotionStarted);
            PartEvent stopped = Assert.Single(events, e => e.eventType == PartEventType.RoboticMotionStopped);
            Assert.True(stopped.value <= 0.5f);
            Assert.True(stopped.ut > 104.0, "the rotor must not read as stopped while it holds 230 RPM");
            Assert.Contains(events, e => e.eventType == PartEventType.RoboticPositionSample
                && System.Math.Abs(e.value - 230f) < 0.5f);
        }

        [Fact]
        public void ServoRateOfMotionReflection_FindsTheStockFloatField()
        {
            // The live read names an internal Assembly-CSharp field; a renamed or retyped field
            // would silently send every rotor back to the stale currentRPM.
            System.Reflection.FieldInfo field = FlightRecorder.ServoRateOfMotionReflection.Field;

            Assert.NotNull(field);
            Assert.Equal(typeof(float), field.FieldType);
            Assert.False(field.IsStatic);
        }

        [Theory]
        [InlineData("ModuleRoboticServoHinge")]
        [InlineData("ModuleRoboticRotationServo")]
        [InlineData("ModuleRoboticServoPiston")]
        public void RoboticCore_OnlyTheRotorReadsTheRateOfMotion(string moduleName)
        {
            // A hinge, rotation servo or piston records a pose, not a speed.
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentAngle", 5f)
                .WithFloat("currentExtension", 0.5f)
                .WithServoRateOfMotionRpm(60f);

            FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, moduleName, out _, out _, out string sourceField);

            Assert.NotEqual("servoRateOfMotion", sourceField);
            Assert.Equal(0, fields.ServoRateOfMotionReadCount);
        }

        [Fact]
        public void RoboticCore_UnsampleableModuleReportsNoPositionButStillYieldsADeadband()
        {
            var fields = new FakeModuleFieldValues();

            bool ok = FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoHinge",
                out float positionValue, out float deadband, out string sourceField);

            Assert.False(ok);
            Assert.Null(sourceField);
            Assert.Equal(0f, positionValue);
            Assert.True(deadband > 0f);
        }

        #endregion
    }
}
