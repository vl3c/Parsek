using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design section 2.12 Rule 3 follow-ups: the invoke hold also takes a vessel a committed
    /// flight docks with or boards (REFLY-HOLD-FEED-MISSES-DOCK-AND-BOARD), and the held entries
    /// are saved with the session marker so a reload redraws them
    /// (REFLY-INVOKE-HOLDS-NOT-REDRAWN-AFTER-RELOAD).
    /// </summary>
    public class ReFlyInvokeHoldGapsTests
    {
        private const string GuidA = "11111111111111111111111111111111";
        private const string GuidB = "22222222222222222222222222222222";
        private const double RewindUT = 1000.0;

        private static BranchPoint Bp(string id, BranchPointType type, double ut, uint target,
            string targetGuid, params string[] parents)
        {
            var bp = new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                TargetVesselPersistentId = target,
                TargetVesselGuid = targetGuid,
            };
            bp.ParentRecordingIds.AddRange(parents);
            return bp;
        }

        private static RecordingTree Tree(string id, params BranchPoint[] bps)
        {
            var tree = new RecordingTree { Id = id };
            tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static List<ReFlyInvokeHold.ChangeClaim> Claims(params RecordingTree[] trees)
        {
            int skipped;
            return ReFlyInvokeHold.CollectChangeClaims(trees, RewindUT, null, out skipped);
        }

        // catches REFLY-HOLD-FEED-MISSES-DOCK-AND-BOARD: a survivor a committed flight docks with
        // after the rewind UT was held only through the ghost chain, which drops the claims of a
        // tree whose leaves have all ended.
        [Fact]
        public void DockOntoSurvivorAfterRewind_IsHeldUntilTheDock()
        {
            var claims = Claims(Tree("tug", Bp("bp_dock", BranchPointType.Dock, 1500.0, 42u, GuidA, "rec_tug")));

            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, new HashSet<uint>(),
                new List<Recording>(), new List<GameAction>(), claims, RewindUT, out matchedWithoutFuture);

            var entry = Assert.Single(held);
            Assert.Equal(42u, entry.Pid);
            Assert.Equal(1500.0, entry.FirstClaimUT);
            Assert.Equal("rec_tug", entry.RecordingId);
            Assert.Equal(ReFlyInvokeHold.EvidenceChanges, entry.Evidence);
            Assert.Empty(entry.FlyingRecordingIds);
            Assert.Equal(0, matchedWithoutFuture);
        }

        [Fact]
        public void BoardOntoSurvivorAfterRewind_IsHeld()
        {
            var claims = Claims(Tree("eva", Bp("bp_board", BranchPointType.Board, 1200.0, 42u, GuidA, "rec_kerbal")));

            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null, null, null, claims, RewindUT,
                out matchedWithoutFuture);

            Assert.Equal(1200.0, Assert.Single(held).FirstClaimUT);
        }

        [Fact]
        public void DockBeforeRewind_HoldsNothing_AndCountsTheMatch()
        {
            var tree = Tree("tug", Bp("bp_dock", BranchPointType.Dock, 900.0, 42u, GuidA, "rec_tug"));
            var claim = new ReFlyInvokeHold.ChangeClaim
            {
                Pid = 42u, LaunchGuid = GuidA, UT = 900.0, RecordingId = "rec_tug", Kind = "dock",
            };

            Assert.Empty(Claims(tree));

            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null, null, null,
                new List<ReFlyInvokeHold.ChangeClaim> { claim }, RewindUT, out matchedWithoutFuture);
            Assert.Empty(held);
            Assert.Equal(1, matchedWithoutFuture);
        }

        [Fact]
        public void DockOntoAnotherLaunchOrAnUnknownLaunch_HoldsNothing()
        {
            var claims = Claims(Tree("tug", Bp("bp_dock", BranchPointType.Dock, 1500.0, 42u, GuidB, "rec_tug")));

            int matchedWithoutFuture;
            Assert.Empty(ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null, null, null, claims, RewindUT,
                out matchedWithoutFuture));
            Assert.Empty(ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, null) }, null, null, null, claims, RewindUT,
                out matchedWithoutFuture));
        }

        [Fact]
        public void ExcludedSurvivor_IsNeverHeldForADock()
        {
            var claims = Claims(Tree("tug", Bp("bp_dock", BranchPointType.Dock, 1500.0, 42u, GuidA, "rec_tug")));

            int matchedWithoutFuture;
            Assert.Empty(ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, new HashSet<uint> { 42u }, null, null, claims,
                RewindUT, out matchedWithoutFuture));
        }

        [Fact]
        public void EarlierFlightWins_OverALaterDock()
        {
            var later = new Recording
            {
                RecordingId = "rec_own", VesselPersistentId = 42u, RecordedVesselGuid = GuidA,
                ExplicitStartUT = 1100.0, ExplicitEndUT = 1300.0,
            };
            later.TerminalStateValue = TerminalState.Orbiting;
            var claims = Claims(Tree("tug", Bp("bp_dock", BranchPointType.Dock, 1500.0, 42u, GuidA, "rec_tug")));

            int matchedWithoutFuture;
            var entry = Assert.Single(ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null, new List<Recording> { later }, null,
                claims, RewindUT, out matchedWithoutFuture));

            Assert.Equal(1100.0, entry.FirstClaimUT);
            Assert.Equal(ReFlyInvokeHold.EvidenceFlies, entry.Evidence);
            Assert.Equal(new[] { "rec_own" }, entry.FlyingRecordingIds);
        }

        [Fact]
        public void CollectChangeClaims_KeepsDockAndBoardOnly_AndDropsSkippedClaimants()
        {
            var tree = Tree("t",
                Bp("bp_dock", BranchPointType.Dock, 1500.0, 42u, GuidA, "rec_tug"),
                Bp("bp_board", BranchPointType.Board, 1600.0, 43u, GuidB, "rec_kerbal"),
                Bp("bp_undock", BranchPointType.Undock, 1700.0, 44u, GuidA, "rec_tug"),
                Bp("bp_notarget", BranchPointType.Dock, 1800.0, 0u, null, "rec_tug"),
                Bp("bp_noparent", BranchPointType.Dock, 1900.0, 45u, GuidA),
                Bp("bp_replaced", BranchPointType.Dock, 2000.0, 46u, GuidA, "rec_replaced"));

            int skipped;
            var claims = ReFlyInvokeHold.CollectChangeClaims(
                new List<RecordingTree> { tree }, RewindUT, id => id == "rec_replaced", out skipped);

            Assert.Equal(2, claims.Count);
            Assert.Equal("dock", claims[0].Kind);
            Assert.Equal(42u, claims[0].Pid);
            Assert.Equal(GuidA, claims[0].LaunchGuid);
            Assert.Equal("board", claims[1].Kind);
            Assert.Equal("rec_kerbal", claims[1].RecordingId);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void CollectChangeClaims_TargetGuidFallsBackToTheClaimingTreesRecording()
        {
            var tree = Tree("t", Bp("bp_dock", BranchPointType.Dock, 1500.0, 42u, null, "rec_tug"));
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "rec_station", VesselPersistentId = 42u, RecordedVesselGuid = GuidA,
                ExplicitStartUT = 100.0, ExplicitEndUT = 200.0,
            });

            var claim = Assert.Single(Claims(tree));

            Assert.Equal(GuidA, VesselLaunchIdentity.NormalizeGuid(claim.LaunchGuid));
        }

        [Fact]
        public void Entry_SavesAndLoadsWithTheMarker()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1", TreeId = "tree_1", ActiveReFlyRecordingId = "rec_prov",
                OriginChildRecordingId = "rec_origin", RewindPointId = "rp_1", InvokedUT = 1000.0,
            };
            var flies = new ReFlyInvokeHold.Entry
            {
                Pid = 42u, LaunchGuid = GuidA, RecordingId = "rec_later", FirstClaimUT = 1500.25,
                Evidence = ReFlyInvokeHold.EvidenceFlies,
            };
            flies.FlyingRecordingIds.Add("rec_later");
            flies.FlyingRecordingIds.Add("rec_later2");
            marker.InvokeHolds.Add(flies);
            marker.InvokeHolds.Add(new ReFlyInvokeHold.Entry
            {
                Pid = 43u, LaunchGuid = null, RecordingId = "rec_tug", FirstClaimUT = 1700.0,
                Evidence = ReFlyInvokeHold.EvidenceChanges,
            });

            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);
            var restored = ReFlySessionMarker.LoadFrom(parent.GetNode(ReFlySessionMarker.NodeName));

            Assert.Equal(2, restored.InvokeHolds.Count);
            var a = restored.InvokeHolds[0];
            Assert.Equal(42u, a.Pid);
            Assert.Equal(GuidA, a.LaunchGuid);
            Assert.Equal("rec_later", a.RecordingId);
            Assert.Equal(1500.25, a.FirstClaimUT);
            Assert.Equal(ReFlyInvokeHold.EvidenceFlies, a.Evidence);
            Assert.Equal(new[] { "rec_later", "rec_later2" }, a.FlyingRecordingIds);
            var b = restored.InvokeHolds[1];
            Assert.Equal(43u, b.Pid);
            Assert.Null(b.LaunchGuid);
            Assert.Equal(ReFlyInvokeHold.EvidenceChanges, b.Evidence);
            Assert.Empty(b.FlyingRecordingIds);
        }

        [Fact]
        public void MarkerWithoutHolds_LoadsAnEmptyList()
        {
            var marker = new ReFlySessionMarker { SessionId = "sess_1", InvokedUT = 1000.0 };
            var parent = new ConfigNode("PARSEK");
            marker.SaveInto(parent);

            var node = parent.GetNode(ReFlySessionMarker.NodeName);
            Assert.False(node.HasNode(ReFlyInvokeHold.EntryNodeName));
            Assert.Empty(ReFlySessionMarker.LoadFrom(node).InvokeHolds);
        }

        [Fact]
        public void LoadEntry_RejectsANodeWithNoVesselOrNoClaimUT()
        {
            var noPid = new ConfigNode(ReFlyInvokeHold.EntryNodeName);
            noPid.AddValue("firstClaimUT", "1500");
            var noUt = new ConfigNode(ReFlyInvokeHold.EntryNodeName);
            noUt.AddValue("pid", "42");

            Assert.Null(ReFlyInvokeHold.LoadEntry(noPid));
            Assert.Null(ReFlyInvokeHold.LoadEntry(noUt));
        }

        private static ConfigNode Vessel(uint pid, string guid, string name)
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("pid", guid ?? "");
            v.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            v.AddValue("name", name);
            return v;
        }

        [Fact]
        public void FindHeldVesselNode_MatchesThePidAndRefusesAnotherLaunch()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddNode(Vessel(41u, GuidA, "Other"));
            fs.AddNode(Vessel(42u, GuidB, "Relaunch"));
            fs.AddNode(Vessel(42u, GuidA, "Station"));

            Assert.Equal("Station", ReFlyInvokeHold.FindHeldVesselNode(fs, 42u, GuidA).GetValue("name"));
            Assert.Null(ReFlyInvokeHold.FindHeldVesselNode(fs, 43u, GuidA));
            Assert.Null(ReFlyInvokeHold.FindHeldVesselNode(null, 42u, GuidA));
        }

        [Theory]
        [InlineData(1600.0, false, false, null, true, "claim-passed")]
        [InlineData(1100.0, true, false, null, true, "already-held")]
        [InlineData(1100.0, false, true, GuidB, true, "other-launch")]
        [InlineData(1100.0, false, true, GuidA, false, "hold-live")]
        [InlineData(1100.0, false, true, null, false, "hold-live")]
        [InlineData(1100.0, false, false, null, true, "restore")]
        [InlineData(1100.0, false, false, null, false, "no-snapshot")]
        public void DecideReloadAction_TruthTable(
            double currentUT, bool ghosted, bool live, string liveGuid, bool hasCopy, string expected)
        {
            var entry = new ReFlyInvokeHold.Entry { Pid = 42u, LaunchGuid = GuidA, FirstClaimUT = 1500.0 };

            Assert.Equal(expected,
                ReFlyInvokeHold.DecideReloadAction(entry, currentUT, ghosted, live, liveGuid, hasCopy));
        }

        [Fact]
        public void TrackHeldVesselWithoutLiveVessel_TracksOnceAndNeedsASnapshot()
        {
            var ghoster = new VesselGhoster();
            var snapshot = Vessel(42u, GuidA, "Station");

            Assert.False(ghoster.TrackHeldVesselWithoutLiveVessel(42u, "Station", null));
            Assert.False(ghoster.TrackHeldVesselWithoutLiveVessel(0u, "Station", snapshot));
            Assert.True(ghoster.TrackHeldVesselWithoutLiveVessel(42u, "Station", snapshot));
            Assert.False(ghoster.TrackHeldVesselWithoutLiveVessel(42u, "Station", snapshot));
            Assert.True(ghoster.IsGhosted(42u));
            Assert.Same(snapshot, ghoster.GetGhostedInfo(42u).snapshot);
            Assert.Equal("Station", ghoster.GetGhostedInfo(42u).vesselName);
        }
    }
}
