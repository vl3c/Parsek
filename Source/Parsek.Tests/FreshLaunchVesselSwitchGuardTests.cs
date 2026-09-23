using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FRESH-LAUNCH-JOINS-RESTORED-COMMITTED-TREE: a FLIGHT->FLIGHT launch made within
    /// the vessel-switch freshness window of flight-scene entry read as a tracking-station
    /// vessel switch, because the scene's INITIAL focus armed the switch flag. The stashed
    /// tree (a committed tree's restore clone) was then reinstalled on the fresh rollout
    /// and the launch recorded inside it. Two guards: the flag arms only after
    /// onFlightReady, and the vessel-switch restore refuses a fresh-rollout active vessel.
    /// </summary>
    [Collection("Sequential")]
    public class FreshLaunchVesselSwitchGuardTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool savedReady;

        public FreshLaunchVesselSwitchGuardTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            savedReady = ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting;
        }

        public void Dispose()
        {
            ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting = savedReady;
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void ShouldArmVesselSwitchFlag_BeforeFlightReady_DoesNotArm()
        {
            // The scene-entry initial focus fires after OnLoad and before onFlightReady.
            Assert.False(ParsekScenario.ShouldArmVesselSwitchFlag(flightSceneReady: false));
        }

        [Fact]
        public void ShouldArmVesselSwitchFlag_AfterFlightReady_Arms()
        {
            // Mirror direction: a real Switch-To reload fires onVesselSwitching in a
            // scene that is already flight-ready, so the #266 stash still sees it.
            Assert.True(ParsekScenario.ShouldArmVesselSwitchFlag(flightSceneReady: true));
        }

        [Fact]
        public void NoteFlightSceneReady_SetsFlagAndLogsOnce()
        {
            ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting = false;

            ParsekScenario.NoteFlightSceneReadyForVesselSwitchFlag();
            ParsekScenario.NoteFlightSceneReadyForVesselSwitchFlag();

            Assert.True(ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting);
            int armedLines = logLines.FindAll(l =>
                l.Contains("[Scenario]")
                && l.Contains("Vessel-switch flag armed for this flight scene")).Count;
            Assert.Equal(1, armedLines);
        }

        [Fact]
        public void ShouldRefuseVesselSwitchRestore_ActiveVesselIsFreshRollout_Refuses()
        {
            Assert.True(ParsekFlight.ShouldRefuseVesselSwitchRestoreForFreshRollout(
                activeVesselPid: 760196917u, sceneEntryFreshRolloutPid: 760196917u));
        }

        [Fact]
        public void ShouldRefuseVesselSwitchRestore_ResumedScene_DoesNotRefuse()
        {
            // A genuine switch reload is RESUME_SAVED_*: no fresh-rollout pid captured.
            Assert.False(ParsekFlight.ShouldRefuseVesselSwitchRestoreForFreshRollout(
                activeVesselPid: 3620499050u, sceneEntryFreshRolloutPid: 0u));
        }

        [Fact]
        public void ShouldRefuseVesselSwitchRestore_DifferentVessel_DoesNotRefuse()
        {
            // The guard rejects only the captured rollout pid; a switch to another
            // vessel in the same scene keeps the tracked / outsider restore.
            Assert.False(ParsekFlight.ShouldRefuseVesselSwitchRestoreForFreshRollout(
                activeVesselPid: 3620499050u, sceneEntryFreshRolloutPid: 760196917u));
        }
    }
}
