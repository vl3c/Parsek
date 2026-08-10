using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public sealed class ReFlySaveScrubTests : IDisposable
    {
        private readonly string tempDir;
        private readonly string tempPath;
        private readonly bool priorSuppressLogging;
        private readonly Action<string> priorTestSink;
        private readonly List<string> logLines = new List<string>();

        public ReFlySaveScrubTests()
        {
            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek_refly_scrub_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            tempPath = Path.Combine(tempDir, "persistent.sfs");
            priorSuppressLogging = ParsekLog.SuppressLogging;
            priorTestSink = ParsekLog.TestSinkForTesting;
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = priorSuppressLogging;
            ParsekLog.TestSinkForTesting = priorTestSink;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_RemovesOtherSlotsButPreservesUnrelatedVessels()
        {
            // 3130558916 = a sibling slot of THIS rewind point -> removed.
            // 2708531065 = in no slot map at all -> unrelated, preserved.
            // 9           = the selected slot -> kept and made active.
            SaveTestGame(
                MakeVessel(3130558916u, "Kerbal X", 100u),
                MakeVessel(2708531065u, "Mun Station", 200u),
                MakeVessel(9u, "Kerbal X Probe", 300u));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_test",
                PidSlotMap = new Dictionary<uint, int>
                {
                    { 3130558916u, 0 },
                    { 9u, 1 },
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    { 100u, 0 },
                    { 300u, 1 },
                },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 1);

            Assert.True(result.Applied);
            Assert.Equal(3, result.VesselCountBefore);
            Assert.Equal(2, result.VesselsKept);
            Assert.Equal(1, result.VesselsPreserved);
            Assert.Equal(1, result.VesselsRemoved);

            // activeVessel must index the SELECTED slot among the survivors, not
            // simply the first survivor - the preserved station precedes it.
            Assert.Equal(1, result.SelectedActiveIndex);

            ConfigNode flightState = LoadFlightState();
            Assert.Equal("1", flightState.GetValue("activeVessel"));
            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            Assert.Equal(2, vessels.Length);
            Assert.Equal("2708531065", vessels[0].GetValue("persistentId"));
            Assert.Equal("9", vessels[1].GetValue("persistentId"));
            Assert.Equal("Kerbal X Probe", vessels[1].GetValue("name"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("preserved=1"));
        }

        [Theory]
        [InlineData("SpaceObject", "Ast. QRV-142")]
        [InlineData("Flag", "Flag")]
        [InlineData("Station", "Mun Station")]
        [InlineData("Probe", "Comms Relay 3")]
        public void ScrubQuicksaveToSelectedSlot_PreservesWorldObjectsOutsideTheSlotSet(
            string vesselType, string vesselName)
        {
            SaveTestGame(
                MakeVessel(5000u, "Selected", 444u),
                MakeVessel(7777u, vesselName, 888u, type: vesselType));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_preserve_world",
                PidSlotMap = new Dictionary<uint, int> { { 5000u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 444u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(1, result.VesselsPreserved);
            Assert.Equal(0, result.VesselsRemoved);

            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Equal(2, vessels.Length);
            Assert.Equal("7777", vessels[1].GetValue("persistentId"));
            Assert.Equal(vesselType, vessels[1].GetValue("type"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_RemovesSerializedGhostNodesRatherThanPreservingThem()
        {
            // A quicksave taken while ghosts were on the map carries them as
            // ordinary VESSEL nodes. They are in no slot map, so the preserve
            // path would resurrect them as real clickable vessels colocated with
            // the player - the bug #587 third-facet symptom.
            SaveTestGame(
                MakeVessel(5000u, "Selected", 444u),
                MakeVessel(8001u, "Ghost: Kerbal X", 901u),
                MakeVessel(8002u, "Mun Station", 902u));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_ghost_nodes",
                PidSlotMap = new Dictionary<uint, int> { { 5000u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 444u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(1, result.GhostNodesRemoved);
            Assert.Equal(1, result.VesselsRemoved);
            // The real station is preserved; only the ghost node went.
            Assert.Equal(1, result.VesselsPreserved);
            Assert.Equal(2, result.VesselsKept);

            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Equal(2, vessels.Length);
            Assert.DoesNotContain(vessels, v => v.GetValue("name") == "Ghost: Kerbal X");
            Assert.Contains(vessels, v => v.GetValue("name") == "Mun Station");
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("ghostNodesRemoved=1"));
        }

        [Fact]
        public void IsSerializedGhostVesselNode_MatchesOnlyTheGhostNamePrefix()
        {
            Assert.True(GhostMapPresence.IsSerializedGhostVesselNode(
                MakeVessel(1u, GhostMapPresence.GhostVesselNamePrefix + "Kerbal X", 2u)));
            Assert.False(GhostMapPresence.IsSerializedGhostVesselNode(
                MakeVessel(1u, "Kerbal X", 2u)));
            // Not a prefix match: the marker has to lead.
            Assert.False(GhostMapPresence.IsSerializedGhostVesselNode(
                MakeVessel(1u, "Probe Ghost: Two", 2u)));
            Assert.False(GhostMapPresence.IsSerializedGhostVesselNode(new ConfigNode("VESSEL")));
            Assert.False(GhostMapPresence.IsSerializedGhostVesselNode(null));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_DoesNotZeroThrottleOnPreservedVessels()
        {
            SaveTestGame(
                MakeVessel(5000u, "Selected", 444u, mainThrottle: "0.85"),
                MakeVessel(7777u, "Unrelated Lander", 888u, mainThrottle: "0.6",
                    wheelThrottle: "0.4"));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_preserve_throttle",
                PidSlotMap = new Dictionary<uint, int> { { 5000u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 444u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(1, result.VesselsPreserved);

            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Equal(2, vessels.Length);
            Assert.Equal("0", vessels[0].GetNode("CTRLSTATE")?.GetValue("mainThrottle"));
            // The unrelated vessel's control state is its own business.
            Assert.Equal("0.6", vessels[1].GetNode("CTRLSTATE")?.GetValue("mainThrottle"));
            Assert.Equal("0.4", vessels[1].GetNode("CTRLSTATE")?.GetValue("wheelThrottle"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_UsesRootPartFallback()
        {
            SaveTestGame(
                MakeVessel(5000u, "Renumbered Selected", 444u),
                MakeVessel(6000u, "Other Slot", 555u));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_root_fallback",
                PidSlotMap = new Dictionary<uint, int>
                {
                    { 1234u, 0 },
                    { 6000u, 1 },
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    { 444u, 0 },
                    { 555u, 1 },
                },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(1, result.VesselsKept);
            Assert.Equal(0, result.VesselsPreserved);
            Assert.Equal(1, result.VesselsRemoved);

            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("5000", vessels[0].GetValue("persistentId"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_SkipsWhenOnlyUnrelatedVesselsSurvive()
        {
            // Regression guard for the new selected-slot guard: the old
            // "VesselsKept == 0" check would now pass on preserved vessels alone
            // and repoint activeVessel at an unrelated survivor.
            SaveTestGame(
                MakeVessel(7777u, "Mun Station", 888u),
                MakeVessel(7778u, "Ast. QRV-142", 889u, type: "SpaceObject"));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_selected_absent",
                PidSlotMap = new Dictionary<uint, int> { { 5000u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 444u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.False(result.Applied);
            Assert.Equal(-1, result.SelectedActiveIndex);
            Assert.Contains(logLines,
                l => l.Contains("[Rewind]") && l.Contains("selected slot vessel not found"));

            // File untouched: both unrelated vessels still present, activeVessel
            // still the fixture's own value.
            ConfigNode flightState = LoadFlightState();
            Assert.Equal(2, flightState.GetNodes("VESSEL").Length);
            Assert.Equal("1", flightState.GetValue("activeVessel"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_ClosesThrottleFieldsOnKeptVessels()
        {
            SaveTestGame(
                MakeVessel(5000u, "Selected A", 444u, mainThrottle: "0.85", wheelThrottle: "0.5",
                    engineCurrentThrottle: "1", independentThrottlePercentage: "25"),
                MakeVessel(5001u, "Selected B", 445u),
                MakeVessel(6000u, "Other", 555u, mainThrottle: "1"));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_throttle_zero",
                PidSlotMap = new Dictionary<uint, int>
                {
                    { 5000u, 0 },
                    { 5001u, 0 },
                    { 6000u, 1 },
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    { 444u, 0 },
                    { 445u, 0 },
                    { 555u, 1 },
                },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(2, result.VesselsKept);
            Assert.Equal(1, result.VesselsRemoved);
            Assert.Equal(6, result.ThrottleResets);

            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Equal(2, vessels.Length);
            Assert.Equal("0", vessels[0].GetNode("CTRLSTATE")?.GetValue("mainThrottle"));
            Assert.Equal("0", vessels[0].GetNode("CTRLSTATE")?.GetValue("wheelThrottle"));
            ConfigNode engineModule = vessels[0].GetNode("PART")?.GetNode("MODULE");
            Assert.Equal("0", engineModule?.GetValue("currentThrottle"));
            Assert.Equal("0", engineModule?.GetValue("independentThrottlePercentage"));
            Assert.Equal("0", vessels[1].GetNode("CTRLSTATE")?.GetValue("mainThrottle"));
            Assert.Equal("0", vessels[1].GetNode("CTRLSTATE")?.GetValue("wheelThrottle"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("throttleResets=6"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_RefreshesRecordingSidecarEpochsFromCurrentSidecar()
        {
            const string recordingId = "rec_refly_epoch";
            SaveTestGameWithRecordingEpoch(
                recordingId,
                sfsSidecarEpoch: 2,
                MakeVessel(5000u, "Selected", 444u),
                MakeVessel(6000u, "Other", 555u));
            WriteTrajectorySidecar(recordingId, sidecarEpoch: 7);
            var rp = new RewindPoint
            {
                RewindPointId = "rp_epoch_refresh",
                PidSlotMap = new Dictionary<uint, int> { { 5000u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 444u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(1, result.SidecarEpochsRefreshed);
            Assert.Equal(0, result.SidecarEpochRefreshSkipped);

            ConfigNode root = ConfigNode.Load(tempPath);
            ConfigNode tree = root.GetNode("RECORDING_TREE");
            Assert.NotNull(tree);
            ConfigNode recNode = tree.GetNode("RECORDING");
            Assert.NotNull(recNode);
            Assert.Equal("7", recNode.GetValue("sidecarEpoch"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_DoesNotDowngradeNewerSfsSidecarEpoch()
        {
            const string recordingId = "rec_refly_epoch_newer_sfs";
            SaveTestGameWithRecordingEpoch(
                recordingId,
                sfsSidecarEpoch: 9,
                MakeVessel(5000u, "Selected", 444u),
                MakeVessel(6000u, "Other", 555u));
            WriteTrajectorySidecar(recordingId, sidecarEpoch: 7);
            var rp = new RewindPoint
            {
                RewindPointId = "rp_epoch_no_downgrade",
                PidSlotMap = new Dictionary<uint, int> { { 5000u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 444u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(0, result.SidecarEpochsRefreshed);
            Assert.Equal(1, result.SidecarEpochRefreshSkipped);

            ConfigNode root = ConfigNode.Load(tempPath);
            ConfigNode tree = root.GetNode("RECORDING_TREE");
            Assert.NotNull(tree);
            ConfigNode recNode = tree.GetNode("RECORDING");
            Assert.NotNull(recNode);
            Assert.Equal("9", recNode.GetValue("sidecarEpoch"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_LeavesFileUntouchedWhenSelectedMissing()
        {
            SaveTestGame(MakeVessel(6000u, "Other", 555u));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_missing",
                PidSlotMap = new Dictionary<uint, int> { { 1234u, 0 } },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.False(result.Applied);
            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("6000", vessels[0].GetValue("persistentId"));
        }

        [Fact]
        public void RequireSelectedSlotScrubApplied_ThrowsWhenSelectedMissing()
        {
            SaveTestGame(MakeVessel(6000u, "Other", 555u));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_missing_throw",
                PidSlotMap = new Dictionary<uint, int> { { 1234u, 0 } },
            };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                RewindInvoker.RequireSelectedSlotScrubApplied(
                    tempPath, rp, selectedSlotIndex: 0));

            Assert.Contains("refusing to load unscrubbed quicksave", ex.Message);
            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("6000", vessels[0].GetValue("persistentId"));
        }

        private void SaveTestGame(params ConfigNode[] vessels)
        {
            var root = new ConfigNode("GAME");
            var flightState = root.AddNode("FLIGHTSTATE");
            flightState.AddValue("UT", "100");
            flightState.AddValue("activeVessel", Math.Max(0, vessels.Length - 1).ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < vessels.Length; i++)
                flightState.AddNode(vessels[i]);
            root.Save(tempPath);
        }

        private void SaveTestGameWithRecordingEpoch(
            string recordingId, int sfsSidecarEpoch, params ConfigNode[] vessels)
        {
            var root = new ConfigNode("GAME");
            var flightState = root.AddNode("FLIGHTSTATE");
            flightState.AddValue("UT", "100");
            flightState.AddValue("activeVessel", Math.Max(0, vessels.Length - 1).ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < vessels.Length; i++)
                flightState.AddNode(vessels[i]);

            var tree = root.AddNode("RECORDING_TREE");
            var rec = tree.AddNode("RECORDING");
            rec.AddValue("recordingId", recordingId);
            rec.AddValue("sidecarEpoch", sfsSidecarEpoch.ToString(CultureInfo.InvariantCulture));
            root.Save(tempPath);
        }

        private string WriteTrajectorySidecar(string recordingId, int sidecarEpoch)
        {
            string relativePath = RecordingPaths.BuildTrajectoryRelativePath(recordingId);
            string path = Path.Combine(tempDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var rec = new Recording { RecordingId = recordingId };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 2.0, bodyName = "Kerbin" });
            RecordingStore.WriteTrajectorySidecar(path, rec, sidecarEpoch);
            return path;
        }

        private ConfigNode LoadFlightState()
        {
            ConfigNode root = ConfigNode.Load(tempPath);
            Assert.NotNull(root);
            ConfigNode flightState = root.GetNode("FLIGHTSTATE");
            Assert.NotNull(flightState);
            return flightState;
        }

        private static ConfigNode MakeVessel(
            uint vesselPid,
            string name,
            uint rootPartPid,
            string mainThrottle = null,
            string wheelThrottle = null,
            string engineCurrentThrottle = null,
            string independentThrottlePercentage = null,
            string type = null)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("persistentId", vesselPid.ToString(CultureInfo.InvariantCulture));
            vessel.AddValue("name", name);
            if (type != null)
                vessel.AddValue("type", type);
            vessel.AddValue("root", "0");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "mk1-3pod");
            part.AddValue("persistentId", rootPartPid.ToString(CultureInfo.InvariantCulture));
            if (engineCurrentThrottle != null || independentThrottlePercentage != null)
            {
                var module = part.AddNode("MODULE");
                module.AddValue("name", "ModuleEnginesFX");
                if (engineCurrentThrottle != null)
                    module.AddValue("currentThrottle", engineCurrentThrottle);
                if (independentThrottlePercentage != null)
                    module.AddValue("independentThrottlePercentage", independentThrottlePercentage);
            }
            if (mainThrottle != null || wheelThrottle != null)
            {
                var ctrlState = vessel.AddNode("CTRLSTATE");
                if (mainThrottle != null)
                    ctrlState.AddValue("mainThrottle", mainThrottle);
                if (wheelThrottle != null)
                    ctrlState.AddValue("wheelThrottle", wheelThrottle);
            }
            return vessel;
        }
    }
}
