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
        private readonly string tempPath;
        private readonly bool priorSuppressLogging;

        public ReFlySaveScrubTests()
        {
            tempPath = Path.Combine(
                Path.GetTempPath(),
                "parsek_refly_scrub_" + Guid.NewGuid().ToString("N") + ".sfs");
            priorSuppressLogging = ParsekLog.SuppressLogging;
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.SuppressLogging = priorSuppressLogging;
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { }
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_RemovesEveryNonSelectedVessel()
        {
            SaveTestGame(
                MakeVessel(3130558916u, "Kerbal X", 100u),
                MakeVessel(2708531065u, "Kerbal X", 200u),
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
            Assert.Equal(1, result.VesselsKept);
            Assert.Equal(2, result.VesselsRemoved);
            Assert.Equal(0, result.SelectedActiveIndex);

            ConfigNode flightState = LoadFlightState();
            Assert.Equal("0", flightState.GetValue("activeVessel"));
            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("9", vessels[0].GetValue("persistentId"));
            Assert.Equal("Kerbal X Probe", vessels[0].GetValue("name"));
        }

        [Fact]
        public void ScrubQuicksaveToSelectedSlot_UsesRootPartFallback()
        {
            SaveTestGame(
                MakeVessel(5000u, "Renumbered Selected", 444u),
                MakeVessel(6000u, "Other", 555u));
            var rp = new RewindPoint
            {
                RewindPointId = "rp_root_fallback",
                PidSlotMap = new Dictionary<uint, int>
                {
                    { 1234u, 0 },
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    { 444u, 0 },
                },
            };

            var result = RewindInvoker.ScrubQuicksaveToSelectedSlotForReFly(
                tempPath, rp, selectedSlotIndex: 0);

            Assert.True(result.Applied);
            Assert.Equal(1, result.VesselsKept);
            Assert.Equal(1, result.VesselsRemoved);

            ConfigNode[] vessels = LoadFlightState().GetNodes("VESSEL");
            Assert.Single(vessels);
            Assert.Equal("5000", vessels[0].GetValue("persistentId"));
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

        private ConfigNode LoadFlightState()
        {
            ConfigNode root = ConfigNode.Load(tempPath);
            Assert.NotNull(root);
            ConfigNode flightState = root.GetNode("FLIGHTSTATE");
            Assert.NotNull(flightState);
            return flightState;
        }

        private static ConfigNode MakeVessel(uint vesselPid, string name, uint rootPartPid)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("persistentId", vesselPid.ToString(CultureInfo.InvariantCulture));
            vessel.AddValue("name", name);
            vessel.AddValue("root", "0");
            var part = vessel.AddNode("PART");
            part.AddValue("name", "mk1-3pod");
            part.AddValue("persistentId", rootPartPid.ToString(CultureInfo.InvariantCulture));
            return vessel;
        }
    }
}
