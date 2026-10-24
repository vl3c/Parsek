using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using Parsek.Patches;
using PreFlightTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-2: a real vessel a committed future flight docks with is held back outside flight
    /// too, so the Tracking Station / Space Center Recover and Delete cannot dispose of it
    /// before the chain tip spawns its recorded merged form (which would duplicate its parts
    /// and pay for them twice). The block reads the flight ghosting predicate.
    /// </summary>
    [Collection("Sequential")]
    public class ClaimedVesselDisposalGateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const uint StationPid = 100u;
        private const uint TugPid = 50u;

        public ClaimedVesselDisposalGateTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ClaimedVesselDisposalGate.ResetForTesting();
        }

        public void Dispose()
        {
            ClaimedVesselDisposalGate.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
        }

        /// <summary>Tug T (pid 50) docks to station S (pid 100) at UT 500; the merged leaf ends at 600.</summary>
        private static Dictionary<uint, GhostChain> StationDockChains(TerminalState? tipTerminal = null)
        {
            var tug = new Recording
            {
                RecordingId = "T", VesselPersistentId = TugPid,
                ExplicitStartUT = 300, ExplicitEndUT = 500,
                ChildBranchPointId = "bp-dock", Points = new List<TrajectoryPoint>()
            };
            var merged = new Recording
            {
                RecordingId = "T-merged", VesselPersistentId = StationPid,
                ExplicitStartUT = 500, ExplicitEndUT = 600,
                TerminalStateValue = tipTerminal,
                ParentBranchPointId = "bp-dock", Points = new List<TrajectoryPoint>()
            };
            var tree = new RecordingTree { Id = "tree-T", RootRecordingId = "T" };
            foreach (var r in new[] { tug, merged })
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock", Type = BranchPointType.Dock, UT = 500,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { "T" },
                ChildRecordingIds = new List<string> { "T-merged" }
            });
            return GhostChainWalker.ComputeAllGhostChains(new List<RecordingTree> { tree }, 0.0);
        }

        [Fact]
        public void StationClaimedByAFutureDock_IsHeld_BeforeTheTipSpawns()
        {
            var chains = StationDockChains();

            Assert.True(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, StationPid, 300, _ => null, out GhostChain claiming));
            Assert.Equal(600.0, claiming.SpawnUT);
            // Between the dock and the tip spawn it is still held (the tip has not spawned yet).
            Assert.True(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, StationPid, 550, _ => null, out _));
        }

        [Fact]
        public void AtOrAfterTheTipSpawn_IsNotHeld()
        {
            var chains = StationDockChains();
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, StationPid, 600, _ => null, out _));
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, StationPid, 700, _ => null, out _));
        }

        [Fact]
        public void TerminatedChain_IsNotHeld_LikeTheFlightLoad()
        {
            // The flight load leaves a terminated chain's vessel real (#174); the block agrees.
            var chains = StationDockChains(TerminalState.Destroyed);
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, StationPid, 300, _ => null, out _));
        }

        [Fact]
        public void UnclaimedVessel_IsNotHeld()
        {
            var chains = StationDockChains();
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, 777u, 300, _ => null, out _));
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                null, StationPid, 300, _ => null, out _));
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, 0u, 300, _ => null, out _));
        }

        [Fact]
        public void FoldedClaim_IsHeld_UnlessTheLiveVesselIsAnotherLaunch()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = StationPid, SpawnUT = 600, TipRecordingId = "tip"
            };
            chain.AddClaimedVessel(300u, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 400);
            var chains = new Dictionary<uint, GhostChain> { { StationPid, chain } };

            Assert.True(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, 300u, 350, _ => null, out _));
            Assert.True(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, 300u, 350, _ => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out _));
            Assert.False(ClaimedVesselDisposalGate.IsHeldByCommittedFuture(
                chains, 300u, 350, _ => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", out _));
        }

        [Fact]
        public void HoldSentence_UsesTheReservationWording()
        {
            Assert.Equal("Used by a later flight until D600, blocked by timeline until then.",
                ReservationExplanation.ClaimedVesselHold(600, ut => "D" + ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal("Used by a later flight, blocked by timeline.",
                ReservationExplanation.ClaimedVesselHold(double.NaN, null));
        }

        [Fact]
        public void LogBlocked_NamesThePidAndTheChainTip()
        {
            var chains = StationDockChains();
            ClaimedVesselDisposalGate.IsHeldByCommittedFuture(chains, StationPid, 300, _ => null, out GhostChain claiming);
            ClaimedVesselDisposalGate.LogBlocked("Tracking Station Recover", StationPid, "Station", claiming);
            Assert.Contains(logLines, l => l.Contains("[" + ClaimedVesselDisposalGate.Tag + "]")
                && l.Contains("Blocked Tracking Station Recover of claimed vessel 'Station'")
                && l.Contains("pid=100") && l.Contains("chainTip=T-merged") && l.Contains("spawnUT=600"));
        }

        // ---- wiring: every greyed control has its click-block on the stock method ----

        private static List<(Type patch, Type target, string method)> HarmonyTargets()
        {
            var result = new List<(Type, Type, string)>();
            foreach (Type t in typeof(ParsekHarmony).Assembly.GetTypes())
            {
                foreach (HarmonyPatch attr in t.GetCustomAttributes(typeof(HarmonyPatch), false))
                {
                    if (attr.info?.declaringType != null)
                        result.Add((t, attr.info.declaringType, attr.info.methodName));
                }
            }
            return result;
        }

        [Theory]
        [InlineData(typeof(SpaceTracking), "SetVessel")]
        [InlineData(typeof(SpaceTracking), "unlockUI")]
        [InlineData(typeof(SpaceTracking), "OnRecoverConfirm")]
        [InlineData(typeof(SpaceTracking), "OnVesselDeleteConfirm")]
        [InlineData(typeof(KSCVesselMarker), "Create")]
        [InlineData(typeof(KSCVesselMarkers), "RecoverVessel")]
        [InlineData(typeof(LaunchSiteClear), "Test")]
        [InlineData(typeof(LaunchSiteClear), "GetProceedOption")]
        [InlineData(typeof(LaunchSiteClear), "GetWarningDescription")]
        [InlineData(typeof(LaunchSiteFacility), "shipSelected")]
        [InlineData(typeof(LaunchSiteFacility), "ClearSite")]
        [InlineData(typeof(LaunchSiteFacility), "launchChecks")]
        public void StockTarget_IsPatched_AndExists(Type stockType, string method)
        {
            Assert.Contains(HarmonyTargets(), x => x.target == stockType && x.method == method);
            Assert.NotNull(AccessTools.Method(stockType, method));
        }

        [Fact]
        public void TrackingStationConfirmPrefixes_CallTheClickBlock()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "Patches", "GhostTrackingStationPatch.cs");
            string src = File.ReadAllText(path);
            Assert.Contains("RefuseTrackingStationDisposal(selected, \"Delete\")", src);
            Assert.Contains("RefuseTrackingStationDisposal(selected, \"Recover\")", src);
        }
    }
}
