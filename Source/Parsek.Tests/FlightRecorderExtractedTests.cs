using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for FlightRecorder extracted methods and logging additions.
    /// Verifies that DecideOnVesselSwitch decisions, FormatCoverageEntries behavior,
    /// and the ShouldRefreshSnapshot guard are correct.
    /// </summary>
    [Collection("Sequential")]
    public class FlightRecorderExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FlightRecorderExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region DecideOnVesselSwitch — decision coverage

        [Fact]
        public void DecideOnVesselSwitch_SamePid_ReturnsNone()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 100, false, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.None, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_DifferentPid_NonEva_NoTree_ReturnsStop()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPid_ReturnsUndockSwitch()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_EvaToEva_ReturnsContinueOnEva()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, currentIsEva: true, recordingStartedAsEva: true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ContinueOnEva, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_EvaToVessel_ReturnsChainToVessel()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, currentIsEva: false, recordingStartedAsEva: true);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ChainToVessel, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_TargetInBackground_ReturnsPromote()
        {
            var tree = new RecordingTree
            {
                Id = "tree_test",
                TreeName = "Test",
                RootRecordingId = "root",
                ActiveRecordingId = "active"
            };
            tree.Recordings["active"] = new Recording
            {
                RecordingId = "active",
                VesselName = "Active"
            };
            tree.Recordings["bg"] = new Recording
            {
                RecordingId = "bg",
                VesselName = "Background",
                VesselPersistentId = 200
            };
            tree.BackgroundMap[200] = "bg";

            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.PromoteFromBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_TargetNotInBackground_ReturnsTransitionToBackground()
        {
            var tree = new RecordingTree
            {
                Id = "tree_test",
                TreeName = "Test",
                RootRecordingId = "root",
                ActiveRecordingId = "active"
            };
            tree.Recordings["active"] = new Recording
            {
                RecordingId = "active",
                VesselName = "Active"
            };

            var result = FlightRecorder.DecideOnVesselSwitch(100, 300, false, false, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        #endregion

        #region ShouldRefreshSnapshot guard

        [Fact]
        public void ShouldRefreshSnapshot_ForceTrue_AlwaysReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(100.0, 101.0, 10.0, force: true));
        }

        [Fact]
        public void ShouldRefreshSnapshot_MinValue_AlwaysReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(double.MinValue, 100.0, 10.0, force: false));
        }

        [Fact]
        public void ShouldRefreshSnapshot_IntervalNotElapsed_ReturnsFalse()
        {
            Assert.False(FlightRecorder.ShouldRefreshSnapshot(100.0, 105.0, 10.0, force: false));
        }

        [Fact]
        public void ShouldRefreshSnapshot_IntervalElapsed_ReturnsTrue()
        {
            Assert.True(FlightRecorder.ShouldRefreshSnapshot(100.0, 111.0, 10.0, force: false));
        }

        #endregion

        #region ClassifyPartDeath — pure static

        [Fact]
        public void ClassifyPartDeath_WithChute_ReturnsParachuteDestroyed()
        {
            var states = new Dictionary<uint, int> { { 42, 2 } }; // deployed chute
            var result = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, states);
            Assert.Equal(PartEventType.ParachuteDestroyed, result);
            Assert.False(states.ContainsKey(42)); // state removed
        }

        [Fact]
        public void ClassifyPartDeath_WithoutChute_ReturnsDestroyed()
        {
            var states = new Dictionary<uint, int>();
            var result = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: false, states);
            Assert.Equal(PartEventType.Destroyed, result);
        }

        [Fact]
        public void ClassifyPartDeath_ChuteNotDeployed_ReturnsDestroyed()
        {
            var states = new Dictionary<uint, int> { { 42, 0 } }; // stowed chute
            var result = FlightRecorder.ClassifyPartDeath(42, hasParachuteModule: true, states);
            Assert.Equal(PartEventType.Destroyed, result);
        }

        #endregion

        #region EncodeEngineKey

        [Fact]
        public void EncodeEngineKey_BasicEncoding()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 0);
            Assert.Equal(100ul * 256, key);
        }

        [Fact]
        public void EncodeEngineKey_WithModuleIndex()
        {
            ulong key = FlightRecorder.EncodeEngineKey(100, 3);
            Assert.Equal(100ul * 256 + 3, key);
        }

        [Fact]
        public void EncodeEngineKey_DifferentParts_DifferentKeys()
        {
            ulong key1 = FlightRecorder.EncodeEngineKey(100, 0);
            ulong key2 = FlightRecorder.EncodeEngineKey(101, 0);
            Assert.NotEqual(key1, key2);
        }

        #endregion

        #region ParseJettisonNames — pure static

        [Fact]
        public void ParseJettisonNames_EmptyString_ReturnsEmpty()
        {
            var result = FlightRecorder.ParseJettisonNames("");
            Assert.Empty(result);
        }

        [Fact]
        public void ParseJettisonNames_SingleName_ReturnsSingle()
        {
            var result = FlightRecorder.ParseJettisonNames("shroud");
            Assert.Single(result);
            Assert.Equal("shroud", result[0]);
        }

        [Fact]
        public void ParseJettisonNames_MultipleNames_ReturnsTrimmed()
        {
            var result = FlightRecorder.ParseJettisonNames("shroud1, shroud2 ,shroud3");
            Assert.Equal(3, result.Length);
            Assert.Equal("shroud1", result[0]);
            Assert.Equal("shroud2", result[1]);
            Assert.Equal("shroud3", result[2]);
        }

        [Fact]
        public void ParseJettisonNames_Whitespace_ReturnsEmpty()
        {
            var result = FlightRecorder.ParseJettisonNames("   ");
            Assert.Empty(result);
        }

        #endregion

        #region IsRoboticModuleName — pure static

        [Fact]
        public void IsRoboticModuleName_Hinge_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticServoHinge"));
        }

        [Fact]
        public void IsRoboticModuleName_Piston_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticServoPiston"));
        }

        [Fact]
        public void IsRoboticModuleName_RotationServo_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticRotationServo"));
        }

        [Fact]
        public void IsRoboticModuleName_Rotor_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleRoboticServoRotor"));
        }

        [Fact]
        public void IsRoboticModuleName_WheelSuspension_ReturnsTrue()
        {
            Assert.True(FlightRecorder.IsRoboticModuleName("ModuleWheelSuspension"));
        }

        [Fact]
        public void IsRoboticModuleName_Unknown_ReturnsFalse()
        {
            Assert.False(FlightRecorder.IsRoboticModuleName("ModuleEngines"));
        }

        #endregion

        #region ComputeRcsPower — pure static

        [Fact]
        public void ComputeRcsPower_ZeroPower_ReturnsZero()
        {
            Assert.Equal(0f, FlightRecorder.ComputeRcsPower(new float[] { 1f }, 0f));
        }

        [Fact]
        public void ComputeRcsPower_EmptyForces_ReturnsZero()
        {
            Assert.Equal(0f, FlightRecorder.ComputeRcsPower(new float[0], 10f));
        }

        [Fact]
        public void ComputeRcsPower_FullThrust_ReturnsOne()
        {
            float result = FlightRecorder.ComputeRcsPower(new float[] { 10f, 10f }, 10f);
            Assert.Equal(1f, result);
        }

        [Fact]
        public void ComputeRcsPower_HalfThrust_ReturnsHalf()
        {
            float result = FlightRecorder.ComputeRcsPower(new float[] { 5f, 5f }, 10f);
            Assert.Equal(0.5, (double)result, 3);
        }

        #endregion

        #region ClassifyGearState — pure static

        [Fact]
        public void ClassifyGearState_Deployed()
        {
            FlightRecorder.ClassifyGearState("Deployed", out bool isDeployed, out bool isRetracted);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void ClassifyGearState_Retracted()
        {
            FlightRecorder.ClassifyGearState("Retracted", out bool isDeployed, out bool isRetracted);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void ClassifyGearState_Deploying_NeitherDeployedNorRetracted()
        {
            FlightRecorder.ClassifyGearState("Deploying", out bool isDeployed, out bool isRetracted);
            Assert.False(isDeployed);
            Assert.False(isRetracted);
        }

        #endregion

        #region ClassifyCargoBayState — pure static

        [Fact]
        public void ClassifyCargoBayState_ClosedPosition1_AtEnd_IsClosed()
        {
            FlightRecorder.ClassifyCargoBayState(1.0f, 1.0f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.True(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_ClosedPosition0_AtEnd_IsOpen()
        {
            FlightRecorder.ClassifyCargoBayState(1.0f, 0.0f, out bool isOpen, out bool isClosed);
            Assert.True(isOpen);
            Assert.False(isClosed);
        }

        [Fact]
        public void ClassifyCargoBayState_MidPosition_NeitherOpenNorClosed()
        {
            FlightRecorder.ClassifyCargoBayState(0.5f, 0.5f, out bool isOpen, out bool isClosed);
            Assert.False(isOpen);
            Assert.False(isClosed);
        }

        #endregion

        #region ClassifyLadderState — pure static

        [Fact]
        public void ClassifyLadderState_FullyExtended()
        {
            FlightRecorder.ClassifyLadderState(1.0f, out bool isExtended, out bool isRetracted);
            Assert.True(isExtended);
            Assert.False(isRetracted);
        }

        [Fact]
        public void ClassifyLadderState_FullyRetracted()
        {
            FlightRecorder.ClassifyLadderState(0.0f, out bool isExtended, out bool isRetracted);
            Assert.False(isExtended);
            Assert.True(isRetracted);
        }

        [Fact]
        public void ClassifyLadderState_MidTransition_Neither()
        {
            FlightRecorder.ClassifyLadderState(0.5f, out bool isExtended, out bool isRetracted);
            Assert.False(isExtended);
            Assert.False(isRetracted);
        }

        #endregion

        #region TryClassifyLadderStateFromEventActivity — pure static

        [Fact]
        public void TryClassifyLadderStateFromEventActivity_CanRetract_IsDeployed()
        {
            bool ok = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: false, canRetract: true, out bool isDeployed, out bool isRetracted);
            Assert.True(ok);
            Assert.True(isDeployed);
            Assert.False(isRetracted);
        }

        [Fact]
        public void TryClassifyLadderStateFromEventActivity_CanExtend_IsRetracted()
        {
            bool ok = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: true, canRetract: false, out bool isDeployed, out bool isRetracted);
            Assert.True(ok);
            Assert.False(isDeployed);
            Assert.True(isRetracted);
        }

        [Fact]
        public void TryClassifyLadderStateFromEventActivity_BothActive_ReturnsFalse()
        {
            bool ok = FlightRecorder.TryClassifyLadderStateFromEventActivity(
                canExtend: true, canRetract: true, out bool isDeployed, out bool isRetracted);
            Assert.False(ok);
        }

        #endregion

        #region ShouldSkipOrbitSegmentForAtmosphere

        [Fact]
        public void ShouldSkipOrbitSegment_BelowAtmosphere_ReturnsTrue()
        {
            // Kerbin: atmosphereDepth = 70000
            Assert.True(FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(true, 50000, 70000));
        }

        [Fact]
        public void ShouldSkipOrbitSegment_AboveAtmosphere_ReturnsFalse()
        {
            Assert.False(FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(true, 80000, 70000));
        }

        [Fact]
        public void ShouldSkipOrbitSegment_NoAtmosphere_ReturnsFalse()
        {
            // Mun has no atmosphere
            Assert.False(FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(false, 5000, 0));
        }

        [Fact]
        public void ShouldSkipOrbitSegment_ExactlyAtBoundary_ReturnsFalse()
        {
            // At exactly atmosphereDepth, altitude is NOT below — no skip
            Assert.False(FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(true, 70000, 70000));
        }

        #endregion
    }
}
