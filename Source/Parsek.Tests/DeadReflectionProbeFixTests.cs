using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P7 / audit S5 — the probe families that resolved nothing on any stock part.
    ///
    /// Every claim these cells pin was established by decompiling KSP 1.12.5's
    /// <c>Assembly-CSharp.dll</c>; the field names, attribute kinds and defaults are quoted in the
    /// XML docs on the tables in <c>FlightRecorder.ReflectionClassifiers.cs</c>. The shared rule
    /// behind all of them is that <c>module.Fields</c> is <c>[KSPField]</c>-only, so a plain public
    /// field is invisible however obvious its name.
    /// </summary>
    public class DeadReflectionProbeFixTests
    {
        #region Control / aero surfaces: the real field is `deploy`

        [Fact]
        public void AnAirbrakeIsClassifiedFromItsRealStockField()
        {
            // ModuleControlSurface: [KSPField(isPersistant = true)] public bool deploy.
            // ModuleAeroSurface inherits it. Before this, none of the probed names existed and
            // every airbrake on every stock craft recorded nothing.
            var deployed = new FakeModuleFieldValues().WithBool("deploy", true);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                deployed, out bool isDeployed, out bool isRetracted));
            Assert.True(isDeployed);
            Assert.False(isRetracted);

            var stowed = new FakeModuleFieldValues().WithBool("deploy", false);
            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                stowed, out isDeployed, out isRetracted));
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void DeployLeadsTheDeployedFamilyTable()
        {
            // It is the only entry that exists on a stock part, so it must not sit behind eight
            // speculative names a mod might also carry.
            Assert.Equal("deploy", FlightRecorder.AeroSurfaceDeployedFieldNames[0]);
        }

        [Fact]
        public void TheControlSurfaceProbeShakesOutTheSameAsTheAeroOne()
        {
            // TryClassifyControlSurfaceState delegates to the aero core; both families share the
            // `deploy` field because ModuleAeroSurface derives from ModuleControlSurface.
            var fields = new FakeModuleFieldValues().WithBool("deploy", true);
            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out _));
            Assert.True(isDeployed);
        }

        [Fact]
        public void ADeployedSurfaceWithAZeroCommandedAngleReadsAsRetracted()
        {
            // deploy == true but the player set the deploy angle to 0: the surface does not move,
            // so recording a DeployableExtended would put a visual change in the recording that
            // the viewer never sees.
            var fields = new FakeModuleFieldValues()
                .WithBool("deploy", true)
                .WithFloat("deployAngle", 0f);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted));
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void ADeployedSurfaceWithARealAngleStaysDeployed()
        {
            // ModuleControlSurface.OnStart resolves the NaN default to ctrlSurfaceRange, which is
            // 15-20 deg on stock parts.
            var fields = new FakeModuleFieldValues()
                .WithBool("deploy", true)
                .WithFloat("deployAngle", 15f);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted));
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void ANegativeDeployAngleIsStillAVisibleDeflection()
        {
            // deployAngleLimits.x is -ctrlSurfaceRange * 1.5, so a negative commanded angle is a
            // normal player setting, not an error. Magnitude is what matters.
            var fields = new FakeModuleFieldValues()
                .WithBool("deploy", true)
                .WithFloat("deployAngle", -20f);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out _));
            Assert.True(isDeployed);
        }

        [Fact]
        public void ANaNDeployAngleDoesNotVetoTheDeploy()
        {
            // deployAngle reads float.NaN until OnStart resolves it. A non-finite angle is no
            // evidence of a zero one, so the veto must not fire on it.
            var fields = new FakeModuleFieldValues()
                .WithBool("deploy", true)
                .WithFloat("deployAngle", float.NaN);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out _));
            Assert.True(isDeployed);
        }

        [Fact]
        public void AnAirbrakesOwnAeroDeployAngleWinsOverTheInheritedDeployAngle()
        {
            // ModuleAeroSurface carries BOTH fields and deliberately switches the inherited
            // deployAngle off in the UI while reading aeroDeployAngle for its real deflection, so
            // a stale inherited value must not decide the verdict.
            var fields = new FakeModuleFieldValues()
                .WithBool("deploy", true)
                .WithFloat("aeroDeployAngle", 0f)
                .WithFloat("deployAngle", 15f);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out bool isRetracted));
            Assert.False(isDeployed);
            Assert.True(isRetracted);
            Assert.Equal("aeroDeployAngle", FlightRecorder.AeroSurfaceDeployAngleFieldNames[0]);
        }

        [Fact]
        public void TheDeployAngleIsNeverUsedAsAStandaloneDeflectionSignal()
        {
            // The whole reason it is a separate table: deployAngle is a tweakable that resolves to
            // a non-zero constant on every stock part, so treating it as "non-zero means deployed"
            // would classify the entire game's control surfaces as permanently out.
            Assert.DoesNotContain("deployAngle", FlightRecorder.AeroSurfaceDeflectionFieldNames);
            Assert.DoesNotContain("aeroDeployAngle", FlightRecorder.AeroSurfaceDeflectionFieldNames);

            var angleOnly = new FakeModuleFieldValues().WithFloat("deployAngle", 15f);
            Assert.False(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                angleOnly, out bool isDeployed, out bool isRetracted));
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void TheAngleVetoIsNotConsultedWhenTheEventStageAlreadyDecided()
        {
            // Stage order is unchanged: a module that DOES expose deploy/retract UI events keeps
            // its old verdict, and the angle table is never read.
            var fields = new FakeModuleFieldValues()
                .WithRetractAvailableOnly()
                .WithFloat("deployAngle", 0f);

            Assert.True(FlightRecorder.TryClassifyAeroSurfaceStateFromFieldValues(
                fields, out bool isDeployed, out _));
            Assert.True(isDeployed);
            Assert.DoesNotContain("deployAngle", fields.ProbedFieldNames);
        }

        #endregion

        #region ModuleAnimateHeat: one typed cast, not a name

        [Fact]
        public void HeatComesFromTheScalarModuleInterfaceWhenTheModuleImplementsIt()
        {
            // ModuleAnimateHeat's animState/inputState are plain public fields on
            // ModuleAnimationSetter with no attribute, so module.Fields cannot see them. The
            // accessor KSP itself uses is IScalarModule.GetScalar => inputState, the already
            // normalized 0..1 heat ratio UpdateHeatEffect writes every frame.
            var fields = new FakeModuleFieldValues().WithScalarModuleScalar(0.75f);

            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField));
            Assert.Equal(0.75, normalizedHeat, 5);
            Assert.Equal(FlightRecorder.AnimateHeatScalarModuleSourceField, sourceField);
        }

        [Fact]
        public void TheScalarModuleAccessorIsConsultedBeforeAnyName()
        {
            var fields = new FakeModuleFieldValues()
                .WithScalarModuleScalar(0.4f)
                .WithFloat("heat", 0.9f);

            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField));
            Assert.Equal(0.4, normalizedHeat, 5);
            Assert.Equal(FlightRecorder.AnimateHeatScalarModuleSourceField, sourceField);
            // Laziness: no name was probed at all.
            Assert.Empty(fields.ProbedFieldNames);
        }

        [Fact]
        public void AModuleThatDoesNotImplementTheInterfaceStillFallsBackToTheNameTable()
        {
            // Kept for modded heat-animation modules that expose a [KSPField] instead.
            var fields = new FakeModuleFieldValues().WithFloat("heat", 0.6f);

            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField));
            Assert.Equal(0.6, normalizedHeat, 5);
            Assert.Equal("heat", sourceField);
            Assert.Equal(1, fields.ScalarModuleReadCount);
        }

        [Fact]
        public void ANonFiniteInterfaceScalarFallsThroughRatherThanBeingReadAsZero()
        {
            var fields = new FakeModuleFieldValues()
                .WithScalarModuleScalar(float.NaN)
                .WithFloat("heat", 0.6f);

            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out string sourceField));
            Assert.Equal(0.6, normalizedHeat, 5);
            Assert.Equal("heat", sourceField);
        }

        [Theory]
        // GetScalar is contractually 0..1 (UpdateHeatEffect clamps before SetScalar), and
        // NormalizeAnimateHeatScalar still guards the ends of that band. Deliberately NOT probed
        // beyond 1.25: past that the shared normalizer switches to its percent-like reading, which
        // is a fallback for modded [KSPField] scalars and not something GetScalar can produce.
        [InlineData(0f, 0d)]
        [InlineData(1f, 1d)]
        [InlineData(-0.5f, 0d)]
        [InlineData(1.2f, 1d)]
        public void TheInterfaceScalarGoesThroughTheSameNormalizer(float raw, double expected)
        {
            var fields = new FakeModuleFieldValues().WithScalarModuleScalar(raw);

            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(
                fields, out float normalizedHeat, out _));
            Assert.Equal(expected, normalizedHeat, 5);
        }

        [Fact]
        public void AHotEnoughInterfaceScalarCrossesTheRecordedHeatThresholds()
        {
            // The point of the fix: the already-built ThermalAnimationHot/Medium/Cold recorder and
            // playback path only ever fires once a real number reaches it.
            var hot = new FakeModuleFieldValues().WithScalarModuleScalar(
                FlightRecorder.AnimateHeatHotThreshold + 0.01f);
            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(hot, out float hotHeat, out _));
            Assert.True(hotHeat >= FlightRecorder.AnimateHeatHotThreshold);

            var medium = new FakeModuleFieldValues().WithScalarModuleScalar(
                FlightRecorder.AnimateHeatMediumThreshold + 0.01f);
            Assert.True(FlightRecorder.TryClassifyAnimateHeatFromFieldValues(medium, out float mediumHeat, out _));
            Assert.True(mediumHeat >= FlightRecorder.AnimateHeatMediumThreshold);
            Assert.True(mediumHeat < FlightRecorder.AnimateHeatHotThreshold);
        }

        #endregion

        #region Piston: currentExtension, not the speed slider

        [Fact]
        public void ThePistonPlanLeadsWithTheLiveExtension()
        {
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoPiston", out float deadband, out string[] planFields);

            // currentExtension is [KSPField(guiActive)] recomputed from the transform geometry, so
            // it sweeps through a stroke; targetExtension is the [KSPAxisField] setpoint that steps.
            Assert.Equal("currentExtension", planFields[0]);
            Assert.Equal("targetExtension", planFields[1]);
            Assert.True(deadband > 0f);
        }

        [Fact]
        public void TheSpeedSliderIsNoLongerReadAsAPose()
        {
            // traverseVelocity is a [KSPAxisField] speed slider (m/s), constant for the whole
            // stroke. It resolved first, so every piston recorded its speed setting as its pose
            // and the working transform fallback was never reached.
            FlightRecorder.ResolveRoboticFieldPlan(
                "ModuleRoboticServoPiston", out _, out string[] planFields);
            Assert.DoesNotContain("traverseVelocity", planFields);

            // targetPosition is `private float` with no attribute — module.Fields can never see it.
            Assert.DoesNotContain("targetPosition", planFields);
        }

        [Fact]
        public void APistonWithOnlyTheSpeedSliderNowReachesTheTransformFallback()
        {
            // The exact pre-fix stock shape: traverseVelocity present, currentExtension absent
            // (a modded piston), plus the real servo transform.
            var fields = new FakeModuleFieldValues()
                .WithFloat("traverseVelocity", 1f)
                .WithVectorMagnitude("servoTransformPosition", 0.6f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoPiston",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("servoTransformPosition", sourceField);
            Assert.Equal(0.6, positionValue, 5);
        }

        [Fact]
        public void AStockPistonReadsItsCurrentExtension()
        {
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentExtension", 0.35f)
                .WithFloat("targetExtension", 1.0f)
                .WithFloat("traverseVelocity", 1f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoPiston",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("currentExtension", sourceField);
            Assert.Equal(0.35, positionValue, 5);
        }

        [Fact]
        public void TargetExtensionBacksUpTheLiveExtensionWhenItIsAbsent()
        {
            var fields = new FakeModuleFieldValues().WithFloat("targetExtension", 0.8f);

            Assert.True(FlightRecorder.TryClassifyRoboticPositionFromFieldValues(
                fields, "ModuleRoboticServoPiston",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("targetExtension", sourceField);
            Assert.Equal(0.8, positionValue, 5);
        }

        #endregion

        #region Wheel suspension: the config constant no longer shadows the live vector

        [Fact]
        public void TheSuspensionPlanNoLongerCarriesTheConfigConstant()
        {
            // suspensionOffset is a plain [KSPField] read once in OnStart to configure the wheel
            // collider. It never moves, and because it resolved it shadowed the live
            // suspensionPos vector.
            FlightRecorder.ResolveWheelRoboticFieldPlan(
                "ModuleWheelSuspension", out _, out string[] planFields);
            Assert.DoesNotContain("suspensionOffset", planFields);
        }

        [Fact]
        public void AStockSuspensionNowReachesTheLiveSuspensionVector()
        {
            // The exact pre-fix stock shape: suspensionOffset present (and constant),
            // suspensionPos the only thing that actually tracks compression.
            var fields = new FakeModuleFieldValues()
                .WithFloat("suspensionOffset", 0.1f)
                .WithVectorMagnitude("suspensionPos", 0.42f);

            Assert.True(FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelSuspension",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("suspensionPos", sourceField);
            Assert.Equal(0.42, positionValue, 5);
        }

        [Fact]
        public void SuspensionStillPrefersAModdedLiveScalarOverTheVector()
        {
            // The remaining names are for modules that expose a genuine travel scalar.
            var fields = new FakeModuleFieldValues()
                .WithFloat("currentSuspensionOffset", 0.2f)
                .WithVectorMagnitude("suspensionPos", 0.42f);

            Assert.True(FlightRecorder.TryClassifyWheelRoboticPositionFromFieldValues(
                fields, "ModuleWheelSuspension",
                out float positionValue, out _, out string sourceField));
            Assert.Equal("currentSuspensionOffset", sourceField);
            Assert.Equal(0.2, positionValue, 5);
        }

        #endregion

        #region ModuleRobotArmScanner: deliberately unchanged

        [Fact]
        public void TheScannerProbeStillResolvesThroughItsInheritedExtendRetractEvents()
        {
            // The audit filed this as a fifth dead probe; it is not. ModuleRobotArmScanner derives
            // from ModuleDeployablePart, whose [KSPEvent] Extend()/Retract() the scanner actively
            // toggles (Events["Extend"].active = true/false). BaseEvent.name is the method name, so
            // the stage-1 keyword match resolves and no name-table change was warranted.
            var extended = new FakeModuleFieldValues().WithRetractAvailableOnly();
            Assert.True(FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                extended, out bool isDeployed, out bool isRetracted));
            Assert.True(isDeployed);
            Assert.False(isRetracted);

            var packed = new FakeModuleFieldValues().WithDeployAvailableOnly();
            Assert.True(FlightRecorder.TryClassifyRobotArmScannerStateFromFieldValues(
                packed, out isDeployed, out isRetracted));
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void NoArmDeployStateNameWasAddedToTheScannerTable()
        {
            // The live ArmDeployState sits behind a `new` property over a private, unattributed
            // _deployState, so it is not in module.Fields at all - and adding any accessor would
            // duplicate CheckDeployableState, which already polls the base ModuleDeployablePart the
            // scanner mirrors every arm state onto.
            Assert.DoesNotContain("deployState", FlightRecorder.RobotArmScannerDeployedFieldNames);
            Assert.DoesNotContain("_deployState", FlightRecorder.RobotArmScannerDeployedFieldNames);
            Assert.DoesNotContain("armState", FlightRecorder.RobotArmScannerDeployedFieldNames);
        }

        #endregion
    }
}
