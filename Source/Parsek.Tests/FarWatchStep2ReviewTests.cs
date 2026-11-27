using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Far watch step 2 review fixes: the camera restore on a watch exit while the drone exists
    /// but is not active, the camera net in every drone state, a chain segment transfer keeping
    /// the follow watch, a lost ship never refusing a save, and the test runner's between-run
    /// reset never leaving a live drone without its identity.
    /// </summary>
    public class FarWatchCameraExitTests
    {
        // Fails if: a watch exit skips the camera restore whenever the drone merely exists, so
        // the camera stays under the ghost while the drone spawns, hands back or tears down.
        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, false, true)]
        [InlineData(true, true, false, true)]
        [InlineData(true, true, true, true)]
        public void SkipRestore_OnlyWhenAskedOrTheDroneIsActive(bool requested, bool exists, bool active, bool skip)
        {
            Assert.Equal(skip, WatchModeController.ShouldSkipCameraRestoreOnWatchExit(requested, exists, active));
        }

        // Fails if: the camera net skips Spawning or Teardown again (the drone exists in both).
        [Fact]
        public void CameraNet_RunsInEveryStateTheDroneMayExistIn()
        {
            foreach (FarWatchState s in Enum.GetValues(typeof(FarWatchState)))
                Assert.Equal(s != FarWatchState.Idle, FarWatchTransitions.CameraNetRuns(s));
        }
    }

    public class FarWatchFollowRekeyTests
    {
        [Fact]
        public void SameRecording_Keeps()
        {
            Assert.Equal(FarWatchFollowRekey.Keep,
                FarWatchTransitions.DecideFollowRekey(true, 3, "rec-a", 3, "rec-a"));
        }

        // Fails if: a chain segment transfer (watch mode moved to the next segment) leaves the
        // drone on the old segment or ends the far watch.
        [Fact]
        public void SegmentTransfer_Rekeys()
        {
            Assert.Equal(FarWatchFollowRekey.Rekey,
                FarWatchTransitions.DecideFollowRekey(true, 4, "rec-b", 3, "rec-a"));
        }

        [Fact]
        public void SameIndexOtherRecording_Rekeys()
        {
            Assert.Equal(FarWatchFollowRekey.Rekey,
                FarWatchTransitions.DecideFollowRekey(true, 3, "rec-b", 3, "rec-a"));
        }

        [Theory]
        [InlineData(false, 3)]
        [InlineData(true, -1)]
        public void NoWatch_IsWatchGone(bool watching, int index)
        {
            Assert.Equal(FarWatchFollowRekey.WatchGone,
                FarWatchTransitions.DecideFollowRekey(watching, index, null, 3, "rec-a"));
        }

        // Fails if: TransferWatchToNextSegment calls ExitWatchMode without the watch-switch flag,
        // so the exit ends the far watch (EndFarWatchOnWatchExit) on every chain boundary.
        [Fact]
        public void SegmentTransfer_ExitsUnderTheWatchSwitchFlag()
        {
            string src = ReadParsekSource("Controllers/WatchModeController.cs");
            int start = src.IndexOf("internal bool TransferWatchToNextSegment(", StringComparison.Ordinal);
            Assert.True(start >= 0, "TransferWatchToNextSegment not found");
            int exit = src.IndexOf("ExitWatchMode(skipCameraRestore: true);", start, StringComparison.Ordinal);
            Assert.True(exit > start, "TransferWatchToNextSegment no longer exits watch mode");
            int flagOn = src.LastIndexOf("exitingForWatchSwitch = true;", exit, StringComparison.Ordinal);
            Assert.True(flagOn > start, "the segment transfer's exit is not under exitingForWatchSwitch");
            int flagOff = src.IndexOf("exitingForWatchSwitch = false;", exit, StringComparison.Ordinal);
            int nextIndex = src.IndexOf("watchedRecordingIndex = nextIndex;", exit, StringComparison.Ordinal);
            Assert.True(flagOff > exit && flagOff < nextIndex, "the watch-switch flag is not cleared right after the exit");
        }

        private static string ReadParsekSource(string relPath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(
                root, "Source", "Parsek", relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }

    [Collection("Sequential")]
    public class FarWatchShipLostSaveTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private static readonly Guid DroneGuid = new Guid("11111111-2222-3333-4444-555555555555");
        private static readonly Guid ShipGuid = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        private static readonly Guid A = new Guid("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid B = new Guid("00000000-0000-0000-0000-00000000000b");
        private const uint DronePid = 5u;

        public FarWatchShipLostSaveTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            CameraDroneIdentity.ResetForTesting();
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            CameraDroneIdentity.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ProtoVessel Pv(uint pid, Guid guid)
        {
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.persistentId = pid;
            pv.vesselID = guid;
            return pv;
        }

        private static FlightState Fs(int activeIdx, params ProtoVessel[] pvs)
        {
            var fs = (FlightState)FormatterServices.GetUninitializedObject(typeof(FlightState));
            fs.protoVessels = new List<ProtoVessel>(pvs);
            fs.activeVesselIdx = activeIdx;
            return fs;
        }

        private static void ArmDrone()
        {
            CameraDroneIdentity.SetShip(77u, ShipGuid);
            CameraDroneIdentity.SetDrone(DronePid, DroneGuid);
        }

        [Fact]
        public void ShipMissing_NotLost_StillRefusesTheSave()
        {
            ArmDrone();
            FlightState fs = Fs(1, Pv(1u, A), Pv(DronePid, DroneGuid));
            Assert.Throws<InvalidOperationException>(() => GhostMapPresence.StripFromSave(fs));
        }

        // Fails if: a save after the ship is lost (stock's flight-ended screen, the pause menu)
        // throws, or keeps the drone, or writes an index naming another vessel.
        [Fact]
        public void ShipLost_ActiveDrone_SaveStripsTheDroneAndWritesMinusOne()
        {
            ArmDrone();
            CameraDroneIdentity.MarkShipLost();
            FlightState fs = Fs(1, Pv(1u, A), Pv(DronePid, DroneGuid), Pv(2u, B));

            int removed = GhostMapPresence.StripFromSave(fs);

            Assert.Equal(1, removed);
            Assert.Equal(2, fs.protoVessels.Count);
            Assert.DoesNotContain(fs.protoVessels, pv => pv.persistentId == DronePid);
            Assert.Equal(-1, fs.activeVesselIdx);
            Assert.True(CameraDroneIdentity.Exists, "a save must not drop the live drone's ids");
            Assert.Contains(logLines, l => l.Contains("[CameraDrone]") && l.Contains("shipLost=True"));
        }

        [Fact]
        public void ShipLost_OtherActive_IndexFollowsItsEntryPastGhostAndDrone()
        {
            ArmDrone();
            CameraDroneIdentity.MarkShipLost();
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(900u);
            FlightState fs = Fs(3, Pv(900u, Guid.Empty), Pv(DronePid, DroneGuid), Pv(1u, A), Pv(2u, B));

            GhostMapPresence.StripFromSave(fs);

            Assert.Equal(2, fs.protoVessels.Count);
            Assert.Equal(1, fs.activeVesselIdx);
            Assert.Equal(2u, fs.protoVessels[fs.activeVesselIdx].persistentId);
        }

        // Fails if: the scene-exit leftover strip keeps the ghost-only arithmetic, which does not
        // count the drone it removes, so the index lands one slot late.
        [Fact]
        public void StripPending_IndexCountsTheRemovedDrone()
        {
            ArmDrone();
            CameraDroneIdentity.MarkStripPending();
            FlightState fs = Fs(2, Pv(1u, A), Pv(DronePid, DroneGuid), Pv(2u, B));

            GhostMapPresence.StripFromSave(fs);

            Assert.Equal(1, fs.activeVesselIdx);
            Assert.Equal(2u, fs.protoVessels[fs.activeVesselIdx].persistentId);
        }

        [Theory]
        // [A, drone, B] active B: one removed before it.
        [InlineData(2, 1, 1, (int)GhostMapPresence.ActiveVesselIdxOutcome.Shifted)]
        // active A: nothing before it.
        [InlineData(0, 0, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.Unchanged)]
        // active the drone itself: -1, never 0.
        [InlineData(1, -1, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.ActiveRemoved)]
        // stock's -1 and past the end are left alone.
        [InlineData(-1, -1, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.OutOfRange)]
        [InlineData(7, 7, 0, (int)GhostMapPresence.ActiveVesselIdxOutcome.OutOfRange)]
        public void ResolveWithoutShip_Table(int oldIdx, int expectedIdx, int expectedRemoved, int expectedOutcome)
        {
            var pids = new List<uint> { 1u, DronePid, 2u };
            var guids = new List<Guid> { A, DroneGuid, B };
            int idx = GhostMapPresence.ResolveActiveVesselIdxAfterStripWithoutShip(
                pids, guids, new HashSet<uint>(), oldIdx, DronePid, DroneGuid,
                out int removed, out GhostMapPresence.ActiveVesselIdxOutcome outcome);
            Assert.Equal(expectedIdx, idx);
            Assert.Equal(expectedRemoved, removed);
            Assert.Equal((GhostMapPresence.ActiveVesselIdxOutcome)expectedOutcome, outcome);
        }

        [Fact]
        public void ShipLost_ClearedWithTheIdentity_AndNeedsADrone()
        {
            CameraDroneIdentity.MarkShipLost();
            Assert.False(CameraDroneIdentity.ShipLost);
            ArmDrone();
            CameraDroneIdentity.MarkShipLost();
            Assert.True(CameraDroneIdentity.SaveMayProceedWithoutShip);
            CameraDroneIdentity.Clear("test");
            Assert.False(CameraDroneIdentity.ShipLost);
            Assert.False(CameraDroneIdentity.SaveMayProceedWithoutShip);
        }

        [Theory]
        [InlineData("Aborted", true, 2, true)]
        [InlineData("Aborted", true, 1, false)]
        [InlineData("Aborted", false, 5, false)]
        [InlineData("Following", true, 5, false)]
        public void ShipLostScreen_OnceAbortedAfterTheDelay(string stateName, bool pending, int frames, bool show)
        {
            var state = (FarWatchState)Enum.Parse(typeof(FarWatchState), stateName);
            Assert.Equal(show, FarWatchTransitions.ShouldShowShipLostScreen(
                state, pending, frames, FarWatchController.ShipLostScreenDelayFrames));
        }
    }

    public class FarWatchTestRunnerResetTests
    {
        // Fails if: the between-run reset destroys (or drops the ids of) an ACTIVE drone with no
        // ship to switch to, or leaves an inactive drone alive.
        [Theory]
        [InlineData(false, false, false, "NoDrone")]
        [InlineData(true, false, false, "DestroyInactiveDrone")]
        [InlineData(true, false, true, "DestroyInactiveDrone")]
        [InlineData(true, true, true, "SwitchToShipThenDestroy")]
        [InlineData(true, true, false, "KeepUntilGone")]
        public void Decision_Table(bool droneExists, bool droneActive, bool shipLoaded, string expected)
        {
            Assert.Equal((FarWatchImmediateEnd)Enum.Parse(typeof(FarWatchImmediateEnd), expected),
                FarWatchTransitions.DecideImmediateEnd(droneExists, droneActive, shipLoaded));
        }

        // Fails if: the runner drops the drone ids even when the far watch could not end.
        [Fact]
        public void Runner_DropsTheIdsOnlyAfterTheWatchEnded()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string src = File.ReadAllText(Path.Combine(root, "Source", "Parsek", "InGameTests", "InGameTestRunner.cs"));
            int call = src.IndexOf("EndForTestRunnerReset(reason)", StringComparison.Ordinal);
            Assert.True(call >= 0, "the runner no longer ends the far watch through EndForTestRunnerReset");
            int guard = src.IndexOf("if (ended)", call, StringComparison.Ordinal);
            int reset = src.IndexOf("CameraDroneIdentity.ResetBetweenTestRuns(reason)", call, StringComparison.Ordinal);
            Assert.True(guard > call && reset > guard, "ResetBetweenTestRuns is not guarded by the far watch ending");
            Assert.DoesNotContain("fw.OnSceneExit()", src);
        }
    }
}
