using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostingTriggerClassifierTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostingTriggerClassifierTests()
        {
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        #region IsGhostingTrigger — cosmetic events (return false)

        [Fact]
        public void IsGhostingTrigger_LightOn_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.LightOn));
        }

        [Fact]
        public void IsGhostingTrigger_LightOff_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.LightOff));
        }

        [Fact]
        public void IsGhostingTrigger_LightBlinkEnabled_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.LightBlinkEnabled));
        }

        [Fact]
        public void IsGhostingTrigger_LightBlinkDisabled_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.LightBlinkDisabled));
        }

        [Fact]
        public void IsGhostingTrigger_LightBlinkRate_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.LightBlinkRate));
        }

        [Fact]
        public void IsGhostingTrigger_ThermalAnimationHot_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ThermalAnimationHot));
        }

        [Fact]
        public void IsGhostingTrigger_ThermalAnimationCold_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ThermalAnimationCold));
        }

        [Fact]
        public void IsGhostingTrigger_ThermalAnimationMedium_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ThermalAnimationMedium));
        }

        #endregion

        #region IsGhostingTrigger — structural/mechanical events (return true)

        [Fact]
        public void IsGhostingTrigger_Decoupled_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.Decoupled));
        }

        [Fact]
        public void IsGhostingTrigger_Destroyed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.Destroyed));
        }

        [Fact]
        public void IsGhostingTrigger_ParachuteDeployed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ParachuteDeployed));
        }

        [Fact]
        public void IsGhostingTrigger_ParachuteCut_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ParachuteCut));
        }

        [Fact]
        public void IsGhostingTrigger_ShroudJettisoned_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ShroudJettisoned));
        }

        [Fact]
        public void IsGhostingTrigger_EngineIgnited_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.EngineIgnited));
        }

        [Fact]
        public void IsGhostingTrigger_EngineShutdown_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.EngineShutdown));
        }

        [Fact]
        public void IsGhostingTrigger_EngineThrottle_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.EngineThrottle));
        }

        [Fact]
        public void IsGhostingTrigger_ParachuteDestroyed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ParachuteDestroyed));
        }

        [Fact]
        public void IsGhostingTrigger_DeployableExtended_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.DeployableExtended));
        }

        [Fact]
        public void IsGhostingTrigger_DeployableRetracted_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.DeployableRetracted));
        }

        [Fact]
        public void IsGhostingTrigger_GearDeployed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.GearDeployed));
        }

        [Fact]
        public void IsGhostingTrigger_GearRetracted_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.GearRetracted));
        }

        [Fact]
        public void IsGhostingTrigger_CargoBayOpened_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.CargoBayOpened));
        }

        [Fact]
        public void IsGhostingTrigger_CargoBayClosed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.CargoBayClosed));
        }

        [Fact]
        public void IsGhostingTrigger_FairingJettisoned_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.FairingJettisoned));
        }

        [Fact]
        public void IsGhostingTrigger_RCSActivated_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.RCSActivated));
        }

        [Fact]
        public void IsGhostingTrigger_RCSStopped_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.RCSStopped));
        }

        [Fact]
        public void IsGhostingTrigger_RCSThrottle_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.RCSThrottle));
        }

        [Fact]
        public void IsGhostingTrigger_Docked_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.Docked));
        }

        [Fact]
        public void IsGhostingTrigger_Undocked_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.Undocked));
        }

        [Fact]
        public void IsGhostingTrigger_InventoryPartPlaced_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.InventoryPartPlaced));
        }

        [Fact]
        public void IsGhostingTrigger_InventoryPartRemoved_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.InventoryPartRemoved));
        }

        [Fact]
        public void IsGhostingTrigger_RoboticMotionStarted_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.RoboticMotionStarted));
        }

        [Fact]
        public void IsGhostingTrigger_RoboticPositionSample_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.RoboticPositionSample));
        }

        [Fact]
        public void IsGhostingTrigger_RoboticMotionStopped_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.RoboticMotionStopped));
        }

        [Fact]
        public void IsGhostingTrigger_ParachuteSemiDeployed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(PartEventType.ParachuteSemiDeployed));
        }

        [Fact]
        public void IsGhostingTrigger_UnknownValue_ReturnsTrueAndLogs()
        {
            var unknown = (PartEventType)999;
            Assert.True(GhostingTriggerClassifier.IsGhostingTrigger(unknown));
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("unknown PartEventType") && l.Contains("999"));
        }

        #endregion

        #region IsClaimingBranchPoint — claiming events (return true)

        [Fact]
        public void IsClaimingBranchPoint_Dock_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.Dock));
        }

        [Fact]
        public void IsClaimingBranchPoint_Board_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.Board));
        }

        [Fact]
        public void IsClaimingBranchPoint_Undock_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.Undock));
        }

        [Fact]
        public void IsClaimingBranchPoint_EVA_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.EVA));
        }

        [Fact]
        public void IsClaimingBranchPoint_JointBreak_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.JointBreak));
        }

        #endregion

        #region IsClaimingBranchPoint — non-claiming events (return false)

        [Fact]
        public void IsClaimingBranchPoint_Launch_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.Launch));
        }

        [Fact]
        public void IsClaimingBranchPoint_Terminal_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.Terminal));
        }

        [Fact]
        public void IsClaimingBranchPoint_Breakup_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsClaimingBranchPoint(BranchPointType.Breakup));
        }

        [Fact]
        public void IsClaimingBranchPoint_UnknownValue_ReturnsFalse()
        {
            var unknown = (BranchPointType)999;
            Assert.False(GhostingTriggerClassifier.IsClaimingBranchPoint(unknown));
        }

        #endregion

        #region IsGhostingSegmentEvent — structural events (return true)

        [Fact]
        public void IsGhostingSegmentEvent_PartDestroyed_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.PartDestroyed));
        }

        [Fact]
        public void IsGhostingSegmentEvent_PartRemoved_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.PartRemoved));
        }

        [Fact]
        public void IsGhostingSegmentEvent_PartAdded_ReturnsTrue()
        {
            Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.PartAdded));
        }

        #endregion

        #region IsGhostingSegmentEvent — non-structural events (return false)

        [Fact]
        public void IsGhostingSegmentEvent_ControllerChange_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.ControllerChange));
        }

        [Fact]
        public void IsGhostingSegmentEvent_ControllerDisabled_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.ControllerDisabled));
        }

        [Fact]
        public void IsGhostingSegmentEvent_ControllerEnabled_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.ControllerEnabled));
        }

        [Fact]
        public void IsGhostingSegmentEvent_CrewLost_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.CrewLost));
        }

        [Fact]
        public void IsGhostingSegmentEvent_CrewTransfer_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.CrewTransfer));
        }

        [Fact]
        public void IsGhostingSegmentEvent_TimeJump_ReturnsFalse()
        {
            Assert.False(GhostingTriggerClassifier.IsGhostingSegmentEvent(SegmentEventType.TimeJump));
        }

        [Fact]
        public void IsGhostingSegmentEvent_UnknownValue_ReturnsTrueAndLogs()
        {
            var unknown = (SegmentEventType)999;
            Assert.True(GhostingTriggerClassifier.IsGhostingSegmentEvent(unknown));
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("unknown SegmentEventType") && l.Contains("999"));
        }

        #endregion

        #region HasGhostingTriggerEvents — compound tests

        [Fact]
        public void HasGhostingTriggerEvents_EmptyRecording_ReturnsFalse()
        {
            var rec = new Recording();
            Assert.False(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("HasGhostingTriggerEvents") && l.Contains("found=False"));
        }

        [Fact]
        public void HasGhostingTriggerEvents_LightsOnly_ReturnsFalse()
        {
            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.LightOn, ut = 1.0 });
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.LightOff, ut = 2.0 });
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.LightBlinkEnabled, ut = 3.0 });
            Assert.False(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
            Assert.Contains(logLines, l =>
                l.Contains("found=False") && l.Contains("3 part events"));
        }

        [Fact]
        public void HasGhostingTriggerEvents_EngineIgnition_ReturnsTrue()
        {
            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.EngineIgnited, ut = 1.0 });
            Assert.True(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
            Assert.Contains(logLines, l =>
                l.Contains("found=True") && l.Contains("1 part events"));
        }

        [Fact]
        public void HasGhostingTriggerEvents_MixedEventsWithOneTrigger_ReturnsTrue()
        {
            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.LightOn, ut = 1.0 });
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.ThermalAnimationHot, ut = 2.0 });
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.FairingJettisoned, ut = 3.0 });
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.LightOff, ut = 4.0 });
            Assert.True(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
        }

        [Fact]
        public void HasGhostingTriggerEvents_SegmentEventPartDestroyed_ReturnsTrue()
        {
            var rec = new Recording();
            // No triggering part events
            rec.PartEvents.Add(new PartEvent { eventType = PartEventType.LightOn, ut = 1.0 });
            // But a structural segment event
            rec.SegmentEvents.Add(new SegmentEvent { type = SegmentEventType.PartDestroyed, ut = 2.0 });
            Assert.True(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
            Assert.Contains(logLines, l =>
                l.Contains("found=True") && l.Contains("1 segment events"));
        }

        [Fact]
        public void HasGhostingTriggerEvents_SegmentEventCrewTransferOnly_ReturnsFalse()
        {
            var rec = new Recording();
            rec.SegmentEvents.Add(new SegmentEvent { type = SegmentEventType.CrewTransfer, ut = 1.0 });
            Assert.False(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
            Assert.Contains(logLines, l =>
                l.Contains("found=False") && l.Contains("0 part events") && l.Contains("1 segment events"));
        }

        [Fact]
        public void HasGhostingTriggerEvents_NullPartEvents_HandlesGracefully()
        {
            var rec = new Recording();
            rec.PartEvents = null;
            rec.SegmentEvents = null;
            Assert.False(GhostingTriggerClassifier.HasGhostingTriggerEvents(rec));
            Assert.Contains(logLines, l =>
                l.Contains("found=False") && l.Contains("0 part events") && l.Contains("0 segment events"));
        }

        [Fact]
        public void HasGhostingTriggerEvents_LogsRecordingId()
        {
            var rec = new Recording();
            rec.RecordingId = "test-rec-42";
            GhostingTriggerClassifier.HasGhostingTriggerEvents(rec);
            Assert.Contains(logLines, l =>
                l.Contains("rec=test-rec-42"));
        }

        #endregion
    }
}
