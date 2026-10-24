using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-9: stock's launch-site clear (the editor's "Recover and launch", the Space Center
    /// launch-site dialog) may not recover a vessel a committed future flight holds; the
    /// chain tip would spawn its parts again and a second recovery would pay twice. The
    /// decision reads <see cref="ClaimedVesselDisposalGate"/>'s flight predicate.
    /// </summary>
    [Collection("Sequential")]
    public class ClaimedVesselLaunchSiteTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const uint PlanePid = 100u;
        private const uint FoldedPid = 300u;
        private const uint RoverPid = 777u;
        private const string FoldedGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        public ClaimedVesselLaunchSiteTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ClaimedVesselDisposalGate.ResetForTesting();
            ClaimedVesselLaunchSite.ResetForTesting();
        }

        public void Dispose()
        {
            ClaimedVesselDisposalGate.ResetForTesting();
            ClaimedVesselLaunchSite.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
        }

        /// <summary>Plane P (pid 100) is a chain whose tip spawns at UT 600 and folds in pid 300.</summary>
        private static Dictionary<uint, GhostChain> PlaneChain()
        {
            var chain = new GhostChain { OriginalVesselPid = PlanePid, SpawnUT = 600, TipRecordingId = "tip" };
            chain.AddClaimedVessel(FoldedPid, FoldedGuid, 400);
            return new Dictionary<uint, GhostChain> { { PlanePid, chain } };
        }

        private static ClaimedVesselDisposalGate.SiteVessel Site(uint pid, string guid, string name)
        {
            return new ClaimedVesselDisposalGate.SiteVessel { Pid = pid, Guid = guid, Name = name };
        }

        private static string Date(double ut) => "D" + ut.ToString("F0", CultureInfo.InvariantCulture);

        [Fact]
        public void HeldVesselAmongTheObstructions_RemovesTheProceedOption_AndSaysWhy()
        {
            var site = new List<ClaimedVesselDisposalGate.SiteVessel>
            {
                Site(RoverPid, null, "Rover"),
                Site(PlanePid, null, "Plane"),
            };
            List<ClaimedVesselDisposalGate.HeldVessel> held =
                ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 300, site, null);

            Assert.Single(held);
            Assert.Equal(PlanePid, held[0].Pid);
            Assert.Equal("Plane", held[0].Name);
            Assert.Equal(600.0, held[0].Claiming.SpawnUT);

            Assert.False(ClaimedVesselLaunchSite.TestResult(true, held.Count));
            Assert.False(ClaimedVesselLaunchSite.TestResult(false, held.Count));
            // Editor (PreFlightCheck): a null proceed option is stock's abort-only dialog.
            Assert.Null(ClaimedVesselLaunchSite.ProceedOption("Recover and launch", held.Count, false));
            // Space Center dialog: label kept, the button is greyed instead.
            Assert.Equal("Recover and launch",
                ClaimedVesselLaunchSite.ProceedOption("Recover and launch", held.Count, true));
            Assert.Equal(
                "Plane is on the Runway.\n\n'Plane': Used by a later flight until D600, blocked by timeline until then.",
                ClaimedVesselLaunchSite.WarningDescription("Plane is on the Runway.", held, Date));
        }

        [Fact]
        public void NoHeldVessel_LeavesStockUnchanged()
        {
            var site = new List<ClaimedVesselDisposalGate.SiteVessel> { Site(RoverPid, null, "Rover") };
            List<ClaimedVesselDisposalGate.HeldVessel> held =
                ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 300, site, null);

            Assert.Empty(held);
            Assert.True(ClaimedVesselLaunchSite.TestResult(true, held.Count));
            Assert.False(ClaimedVesselLaunchSite.TestResult(false, held.Count));
            Assert.Equal("Recover and launch",
                ClaimedVesselLaunchSite.ProceedOption("Recover and launch", held.Count, false));
            Assert.Equal("Rover is on the Runway.",
                ClaimedVesselLaunchSite.WarningDescription("Rover is on the Runway.", held, Date));
        }

        [Fact]
        public void AtTheTipSpawn_TheVesselIsNoLongerHeld()
        {
            var site = new List<ClaimedVesselDisposalGate.SiteVessel> { Site(PlanePid, null, "Plane") };
            Assert.Empty(ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 600, site, null));
        }

        [Fact]
        public void FoldedClaim_IsHeldOnlyForItsOwnLaunch()
        {
            var sameLaunch = new List<ClaimedVesselDisposalGate.SiteVessel> { Site(FoldedPid, FoldedGuid, "Lander") };
            var otherLaunch = new List<ClaimedVesselDisposalGate.SiteVessel>
            {
                Site(FoldedPid, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Lander")
            };
            Assert.Single(ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 450, sameLaunch, null));
            Assert.Empty(ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 450, otherLaunch, null));
        }

        [Fact]
        public void TwoHeldVessels_EachGetsItsSentence()
        {
            var site = new List<ClaimedVesselDisposalGate.SiteVessel>
            {
                Site(PlanePid, null, "Plane"),
                Site(FoldedPid, FoldedGuid, "Lander"),
            };
            var held = ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 450, site, null);
            Assert.Equal(2, held.Count);
            Assert.Equal(
                "Stock.\n\n'Plane': Used by a later flight until D600, blocked by timeline until then."
                + "\n'Lander': Used by a later flight until D600, blocked by timeline until then.",
                ClaimedVesselLaunchSite.WarningDescription("Stock.", held, Date));
        }

        [Fact]
        public void LogHeld_NamesEachVesselPidAndName()
        {
            var site = new List<ClaimedVesselDisposalGate.SiteVessel> { Site(PlanePid, null, "Plane") };
            var held = ClaimedVesselDisposalGate.SelectHeld(PlaneChain(), 300, site, null);
            ClaimedVesselLaunchSite.LogHeld("Space Center launch-site Recover", held);
            Assert.Contains(logLines, l => l.Contains("[" + ClaimedVesselDisposalGate.Tag + "]")
                && l.Contains("Blocked Space Center launch-site Recover of claimed vessel 'Plane'")
                && l.Contains("pid=100") && l.Contains("chainTip=tip") && l.Contains("spawnUT=600"));
        }

        [Fact]
        public void FindHeldLive_NoVessels_IsEmpty_AndDoesNotThrow()
        {
            ClaimedVesselDisposalGate.ChainsOverrideForTesting = PlaneChain;
            ClaimedVesselDisposalGate.UtOverrideForTesting = () => 300;
            Assert.Empty(ClaimedVesselDisposalGate.FindHeldLive(null, "test"));
            Assert.Empty(ClaimedVesselDisposalGate.FindHeldLive(new List<ProtoVessel>(), "test"));
        }
    }
}
