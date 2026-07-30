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

        private bool sawDeployEvent;
        private bool sawRetractEvent;
        private bool canDeploy;
        private bool canRetract;

        internal readonly List<string> ProbedFieldNames = new List<string>();
        internal int EventActivityReadCount;
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
