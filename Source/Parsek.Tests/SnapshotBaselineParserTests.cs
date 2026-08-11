using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="GhostVisualBuilder.TryParseSnapshotPartBaseline"/> — the pure
    /// snapshot-module reader behind the M1 ghost build-time baseline. ConfigNode in,
    /// POCO out: no Unity access, so every family is directly testable here.
    ///
    /// The whole point of the nullable fields is that "key absent / unparseable" must be
    /// indistinguishable from pre-M1 behaviour, so a good half of these cells assert
    /// absence rather than a value.
    /// </summary>
    public class SnapshotBaselineParserTests
    {
        private static ConfigNode PartWithModules(
            params (string moduleName, (string key, string value)[] kv)[] modules)
        {
            VesselSnapshotBuilder builder = VesselSnapshotBuilder.ProbeShip("BaselineProbe");
            for (int i = 0; i < modules.Length; i++)
                builder.AddModuleToPart(0, modules[i].moduleName, modules[i].kv);
            return builder.Build().GetNodes("PART")[0];
        }

        private static (string, (string, string)[]) Module(
            string name, params (string, string)[] kv) => (name, kv);

        #region Nothing to read

        [Fact]
        public void NullPartNode_ReturnsNull()
        {
            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(null));
        }

        [Fact]
        public void PartWithNoModules_ReturnsNull()
        {
            ConfigNode partNode = PartWithModules();

            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode));
        }

        [Fact]
        public void ModulesWithNoRecognizedKeys_ReturnsNull()
        {
            // A module the parser knows nothing about, and a known module missing the key
            // it cares about. Both must leave the ghost on its prefab/stow baseline.
            ConfigNode partNode = PartWithModules(
                Module("ModuleCommand", ("controlSrcStatusText", "Fully Powered")),
                Module("ModuleLight"));

            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode));
        }

        [Fact]
        public void MalformedValues_ParseToNoOpinionWithoutThrowing()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleDeployableSolarPanel", ("deployState", "WOBBLY")),
                Module("ModuleWheelDeployment", ("stateString", "Sideways")),
                Module("ModuleAnimationGroup", ("isDeployed", "kinda")),
                Module("ModuleColorChanger", ("animState", "0.00286630238")));

            // deployState present-but-garbage still counts as "this part has a dedicated
            // animate handler", but no family gets an opinion, so the whole baseline is null.
            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode));
        }

        #endregion

        #region Deployables / gear

        [Theory]
        [InlineData("EXTENDED", true)]
        [InlineData("RETRACTED", false)]
        public void DeployState_EndpointsParse(string deployState, bool expected)
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleDeployableSolarPanel", ("deployState", deployState)));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Equal(expected, baseline.deployableExtended);
        }

        [Theory]
        [InlineData("EXTENDING")]
        [InlineData("RETRACTING")]
        [InlineData("BROKEN")]
        public void DeployState_MidTravelOrBroken_NoOpinion(string deployState)
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleDeployableAntenna", ("deployState", deployState)),
                // Second family so the baseline object itself is non-null and the
                // assertion is about deployableExtended, not about HasAnyBaseline.
                Module("ModuleLight", ("isOn", "True")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.False(baseline.deployableExtended.HasValue);
        }

        [Fact]
        public void DeployState_ReadFromAnySubclassNodeName()
        {
            // ModuleDeployablePart is abstract; mods ship their own subclass names.
            // The parser matches on the persisted KEY, not the class name.
            ConfigNode partNode = PartWithModules(
                Module("SomeModdedDeployableThing", ("deployState", "EXTENDED")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.True(baseline.deployableExtended);
        }

        [Theory]
        [InlineData("Deployed", true)]
        [InlineData("Retracted", false)]
        public void GearStateString_EndpointsParse(string stateString, bool expected)
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleWheelDeployment", ("stateString", stateString)));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Equal(expected, baseline.gearDeployed);
        }

        [Fact]
        public void GearStateString_MidTravel_NoOpinion()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleWheelDeployment", ("stateString", "Deploying")),
                Module("ModuleLight", ("isOn", "True")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.False(baseline.gearDeployed.HasValue);
        }

        #endregion

        #region Cargo bays and standalone animations

        [Fact]
        public void CargoBay_PairedNodeAtDeployModuleIndex_ResolvesAgainstClosedPosition()
        {
            // Snapshot MODULE order mirrors part.Modules order, so DeployModuleIndex
            // indexes straight into it. Index 1 is the SECOND animation here — proving
            // the paired node wins over "first ModuleAnimateGeneric".
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimateGeneric", ("animTime", "0")),
                Module("ModuleAnimateGeneric", ("animTime", "1")),
                Module("ModuleCargoBay"));

            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                partNode, cargoBayClosedPosition: 0f, cargoDeployModuleIndex: 1);

            Assert.NotNull(baseline);
            // closedPosition 0 → closed at animTime≈0, open at animTime≈1.
            Assert.True(baseline.cargoBayOpen);
            // The cargo pairing consumes the animation: no standalone opinion.
            Assert.False(baseline.animateGenericDeployed.HasValue);
        }

        [Fact]
        public void CargoBay_InvertedClosedPosition_ResolvesTheOtherWay()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimateGeneric", ("animTime", "1")),
                Module("ModuleCargoBay"));

            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                partNode, cargoBayClosedPosition: 1f, cargoDeployModuleIndex: -1);

            Assert.NotNull(baseline);
            // closedPosition 1 → closed at animTime≈1.
            Assert.False(baseline.cargoBayOpen);
        }

        [Fact]
        public void CargoBay_MidTravelAnimTime_NoOpinion()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimateGeneric", ("animTime", "0.5")),
                Module("ModuleCargoBay"));

            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                partNode, cargoBayClosedPosition: 0f);

            Assert.Null(baseline);
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("0.995", true)]
        [InlineData("0", false)]
        [InlineData("0.005", false)]
        public void StandaloneAnimateGeneric_EndpointsParse(string animTime, bool expected)
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimateGeneric", ("animTime", animTime)));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Equal(expected, baseline.animateGenericDeployed);
        }

        [Fact]
        public void StandaloneAnimateGeneric_MidTravel_NoOpinion()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimateGeneric", ("animTime", "0.4")));

            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode));
        }

        [Fact]
        public void StandaloneAnimateGeneric_SuppressedByDedicatedHandler()
        {
            // Mirror of FlightRecorder.HasDedicatedAnimateHandler: a part whose animation
            // belongs to a ladder / animation group / aero surface must not ALSO get a
            // standalone AnimateGeneric opinion, or the two would fight over the single
            // DeployableGhostInfo the part owns.
            ConfigNode partNode = PartWithModules(
                Module("RetractableLadder"),
                Module("ModuleAnimateGeneric", ("animTime", "1")));

            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode));
        }

        [Fact]
        public void AnimationGroup_IsDeployedParses()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleAnimationGroup", ("isDeployed", "True")),
                Module("ModuleAnimateGeneric", ("animTime", "0")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.True(baseline.animationGroupDeployed);
            Assert.False(baseline.animateGenericDeployed.HasValue);
        }

        #endregion

        #region Parachutes

        [Theory]
        [InlineData("STOWED")]
        [InlineData("ACTIVE")]
        [InlineData("SEMIDEPLOYED")]
        [InlineData("DEPLOYED")]
        [InlineData("CUT")]
        public void ParachutePersistentState_AllFiveStatesCarriedVerbatim(string persistentState)
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleParachute", ("persistentState", persistentState), ("animTime", "0")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Equal(persistentState, baseline.parachutePersistentState);
        }

        [Fact]
        public void ParachutePersistentState_NormalizedToUpperCase()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleParachute", ("persistentState", " semideployed ")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Equal("SEMIDEPLOYED", baseline.parachutePersistentState);
        }

        #endregion

        #region Lights

        [Fact]
        public void ModuleLight_PowerBlinkAndRateAllParse()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleLight",
                    ("isOn", "True"), ("isBlinking", "True"), ("blinkRate", "2.5")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.True(baseline.lightOn);
            Assert.True(baseline.lightBlinking);
            Assert.Equal(2.5f, baseline.lightBlinkRate);
        }

        [Fact]
        public void ModuleLight_ZeroBlinkRate_NoOpinion()
        {
            // Playback substitutes 1 Hz for a non-positive rate; carrying 0 as an opinion
            // would only overwrite a good rate with a bad one.
            ConfigNode partNode = PartWithModules(
                Module("ModuleLight", ("isOn", "False"), ("blinkRate", "0")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.False(baseline.lightOn);
            Assert.False(baseline.lightBlinkRate.HasValue);
        }

        [Fact]
        public void ColorChanger_AnimStateParsesWhenPartHasNoModuleLight()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleColorChanger", ("animState", "True")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.True(baseline.colorChangerOn);
        }

        [Fact]
        public void ColorChanger_SuppressedWhenPartAlsoHasModuleLight()
        {
            // PartStateSeeder.SeedLights only treats a ColorChanger as a cabin light when
            // the part carries no ModuleLight; the baseline mirrors that so one lamp is
            // never described twice.
            ConfigNode partNode = PartWithModules(
                Module("ModuleLight", ("isOn", "False")),
                Module("ModuleColorChanger", ("animState", "True")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.False(baseline.colorChangerOn.HasValue);
            Assert.False(baseline.lightOn);
        }

        #endregion

        #region Robotic ordinal walk

        [Fact]
        public void RoboticOrdinals_CountOnlyRoboticModules()
        {
            // [ModuleLight, hinge, ModuleCommand, piston] → hinge is robotic ordinal 0
            // and piston ordinal 1: the non-robotic modules in between must not shift the
            // numbering, because the recorder counts the same way
            // (FlightRecorder.CacheRoboticModules).
            ConfigNode partNode = PartWithModules(
                Module("ModuleLight", ("isOn", "False")),
                Module("ModuleRoboticServoHinge", ("currentAngle", "42.5")),
                Module("ModuleCommand", ("controlSrcStatusText", "Fully Powered")),
                Module("ModuleRoboticServoPiston", ("currentPosition", "0.75")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.NotNull(baseline.roboticPoses);
            Assert.Equal(2, baseline.roboticPoses.Count);
            Assert.Equal(0, baseline.roboticPoses[0].ordinal);
            Assert.Equal("ModuleRoboticServoHinge", baseline.roboticPoses[0].moduleName);
            Assert.Equal(42.5f, baseline.roboticPoses[0].value);
            Assert.Equal(1, baseline.roboticPoses[1].ordinal);
            Assert.Equal("ModuleRoboticServoPiston", baseline.roboticPoses[1].moduleName);
            Assert.Equal(0.75f, baseline.roboticPoses[1].value);
        }

        [Fact]
        public void RoboticOrdinals_WheelModulesAdvanceTheOrdinalButGetNoPose()
        {
            // Wheel robotics ARE robotic-classified (IsRoboticModuleName), so the recorder
            // counts them. Their poses are deliberately not read — but skipping them
            // silently would slide every later servo's ordinal off by one.
            ConfigNode partNode = PartWithModules(
                Module("ModuleWheelSuspension", ("suspensionPos", "0.3")),
                Module("ModuleRoboticRotationServo", ("currentAngle", "17")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Single(baseline.roboticPoses);
            Assert.Equal(1, baseline.roboticPoses[0].ordinal);
            Assert.Equal("ModuleRoboticRotationServo", baseline.roboticPoses[0].moduleName);
            Assert.Equal(17f, baseline.roboticPoses[0].value);
        }

        [Fact]
        public void Robotic_FallbackFieldUsedWhenPrimaryAbsent()
        {
            // Same first-match-wins probe order the recorder uses
            // (FlightRecorder.ResolveRoboticFieldPlan).
            ConfigNode partNode = PartWithModules(
                Module("ModuleRoboticServoHinge", ("targetAngle", "-12.25")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Single(baseline.roboticPoses);
            Assert.Equal(-12.25f, baseline.roboticPoses[0].value);
        }

        [Fact]
        public void Robotic_PrimaryFieldWinsOverFallback()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleRoboticServoHinge", ("currentAngle", "5"), ("targetAngle", "90")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Equal(5f, baseline.roboticPoses[0].value);
        }

        [Fact]
        public void Rotor_RpmKeyAbsent_NoPoseAtAll()
        {
            // The conservative rotor rule: with no persisted RPM key we say nothing, so a
            // rotor keeps today's stationary-at-spawn behaviour.
            ConfigNode partNode = PartWithModules(
                Module("ModuleRoboticServoRotor", ("motorEnabled", "True")),
                Module("ModuleLight", ("isOn", "True")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Null(baseline.roboticPoses);
        }

        [Fact]
        public void Rotor_RpmKeyPresent_PoseCarriesRpm()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleRoboticServoRotor", ("currentRPM", "48.5")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Single(baseline.roboticPoses);
            Assert.Equal(48.5f, baseline.roboticPoses[0].value);
        }

        [Fact]
        public void Robotic_NonFiniteValue_NoPose()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleRoboticServoHinge", ("currentAngle", "NaN")),
                Module("ModuleLight", ("isOn", "True")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.Null(baseline.roboticPoses);
        }

        #endregion

        #region Whole-part shape

        [Fact]
        public void RealisticPart_AllFamiliesReadInOnePass()
        {
            ConfigNode partNode = PartWithModules(
                Module("ModuleDeployableSolarPanel", ("deployState", "EXTENDED")),
                Module("ModuleWheelDeployment", ("stateString", "Deployed")),
                Module("ModuleParachute", ("persistentState", "SEMIDEPLOYED")),
                Module("ModuleLight", ("isOn", "True"), ("isBlinking", "False"), ("blinkRate", "1")),
                Module("ModuleRoboticServoHinge", ("currentAngle", "33")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.True(baseline.deployableExtended);
            Assert.True(baseline.gearDeployed);
            Assert.Equal("SEMIDEPLOYED", baseline.parachutePersistentState);
            Assert.True(baseline.lightOn);
            Assert.False(baseline.lightBlinking);
            Assert.Equal(1f, baseline.lightBlinkRate);
            Assert.Equal(33f, baseline.roboticPoses.Single().value);
        }

        [Fact]
        public void DuplicateFamilyModules_FirstOneWins()
        {
            // Deployables / gear / lights collapse to one opinion per part per family,
            // matching the single DeployableGhostInfo / LightGhostInfo a part gets.
            ConfigNode partNode = PartWithModules(
                Module("ModuleDeployableSolarPanel", ("deployState", "EXTENDED")),
                Module("ModuleDeployableRadiator", ("deployState", "RETRACTED")));

            SnapshotPartBaseline baseline =
                GhostVisualBuilder.TryParseSnapshotPartBaseline(partNode);

            Assert.NotNull(baseline);
            Assert.True(baseline.deployableExtended);
        }

        #endregion
    }
}
