using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Shared two-tree cross-tree dock fixture (M-MIS-8): partner tree "tb" holds vessel B's
    // pre-dock flight; controller tree "ta" holds A's flight, the foreign Dock branch point
    // claiming B's pid, the combined docked stretch, and the undock fork where B departs.
    internal static class CrossTreeDockFixture
    {
        internal const uint PidA = 100;
        internal const uint PidB = 200;
        internal const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal const string DockBpId = "dockbp";
        internal const double DockUT = 150.0;
        internal const double UndockUT = 300.0;

        internal static Recording Rec(
            string id, uint pid, string guid, string chainId, int chainIndex,
            double start, double end, int pods = 0, int probes = 1,
            string parentBp = null, string childBp = null, bool debris = false,
            string vessel = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vessel ?? ("Vessel " + id),
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = debris,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            return rec;
        }

        internal static BranchPoint BP(
            string id, BranchPointType type, double ut, string[] parents, string[] children,
            uint targetPid = 0, string splitCause = null, string mergeCause = null)
            => new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
                TargetVesselPersistentId = targetPid,
                SplitCause = splitCause,
                MergeCause = mergeCause,
            };

        internal static RecordingTree Tree(string id, Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree { Id = id, RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs)
            {
                r.TreeId = id;
                tree.Recordings[r.RecordingId] = r;
            }
            if (bps != null) tree.BranchPoints.AddRange(bps);
            return tree;
        }

        // Partner tree tb: B's solo pre-dock flight [0..100].
        internal static RecordingTree PartnerTree(string guidB = GuidB)
            => Tree("tb", new[] { Rec("B0", PidB, guidB, "CB", 0, 0, 100) });

        // Controller tree ta: A [0..150] -> Dock(target=B) -> AB [150..300] -> Undock ->
        // { A1 [300..400] (continuing), B1 [300..380] (B departing) }.
        internal static RecordingTree ControllerTree(
            uint mergedPid = PidA, string mergedGuid = GuidA, bool withUndock = true,
            BranchPointType claimType = BranchPointType.Dock)
        {
            var a0 = Rec("A0", PidA, GuidA, "CA", 0, 0, DockUT, pods: 1, probes: 0, childBp: DockBpId);
            var ab = Rec("AB", mergedPid, mergedGuid, "CAB", 0, DockUT, UndockUT,
                pods: 1, probes: 1, parentBp: DockBpId,
                childBp: withUndock ? "undockbp" : null, vessel: "Stack AB");
            var bps = new List<BranchPoint>
            {
                BP(DockBpId, claimType, DockUT, new[] { "A0" }, new[] { "AB" },
                    targetPid: PidB, mergeCause: claimType == BranchPointType.Dock ? "DOCK" : "BOARD"),
            };
            var recs = new List<Recording> { a0, ab };
            if (withUndock)
            {
                // Continuing stack first (recorder convention), departing partner second. When
                // the partner survived as the merged stack (mergedPid == PidB) the continuing
                // child carries B's identity and the departing child is A.
                Recording cont;
                Recording depart;
                if (mergedPid == PidB)
                {
                    cont = Rec("B1", PidB, GuidB, "CB2", 0, UndockUT, 380);
                    depart = Rec("A1", PidA, GuidA, "CA2", 0, UndockUT, 400);
                }
                else
                {
                    cont = Rec("A1", PidA, GuidA, "CA2", 0, UndockUT, 400);
                    depart = Rec("B1", PidB, GuidB, "CB2", 0, UndockUT, 380);
                }
                cont.ParentBranchPointId = "undockbp";
                depart.ParentBranchPointId = "undockbp";
                bps.Add(BP("undockbp", BranchPointType.Undock, UndockUT,
                    new[] { "AB" }, new[] { cont.RecordingId, depart.RecordingId },
                    splitCause: "UNDOCK"));
                recs.Add(cont);
                recs.Add(depart);
            }
            return Tree("ta", recs.ToArray(), bps.ToArray());
        }

        // Controller tree ta with a RE-DOCK: A [0..150] -> Dock(B) -> AB [150..300] ->
        // Undock -> { A1 [300..320], B1 [300..320] } -> Dock (two-parent same-tree merge)
        // -> AB2 [320..400]. A's line carries TWO disjoint docked stretches with A's solo
        // stretch [300..320] between them.
        internal static RecordingTree ControllerTreeWithRedock()
        {
            var ta = ControllerTree();
            var a1 = ta.Recordings["A1"];
            var b1 = ta.Recordings["B1"];
            a1.ExplicitEndUT = 320;
            b1.ExplicitEndUT = 320;
            a1.ChildBranchPointId = "dock2";
            b1.ChildBranchPointId = "dock2";
            var ab2 = Rec("AB2", PidA, GuidA, "CAB2", 0, 320, 400,
                pods: 1, probes: 1, parentBp: "dock2", vessel: "Stack AB2");
            ab2.TreeId = ta.Id;
            ta.Recordings["AB2"] = ab2;
            ta.BranchPoints.Add(BP("dock2", BranchPointType.Dock, 320,
                new[] { "A1", "B1" }, new[] { "AB2" }, mergeCause: "DOCK"));
            return ta;
        }

        // Committed list spanning both trees: [B0, A0, AB, (A1, B1)].
        internal static List<Recording> Committed(RecordingTree tb, RecordingTree ta)
        {
            var committed = new List<Recording>();
            committed.AddRange(tb.Recordings.Values.OrderBy(r => r.RecordingId, StringComparer.Ordinal));
            committed.AddRange(ta.Recordings.Values.OrderBy(r => r.RecordingId, StringComparer.Ordinal));
            return committed;
        }

        internal static Dictionary<string, int> IndexById(List<Recording> committed)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < committed.Count; i++)
                map[committed[i].RecordingId] = i;
            return map;
        }
    }

    [Collection("Sequential")]
    public class MissionCrossTreeDockTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionCrossTreeDockTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---- link derivation ----

        [Fact]
        public void FindLinks_MatchesForeignDockClaim()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();

            var links = MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta });

            var link = Assert.Single(links);
            Assert.Equal(CrossTreeDockFixture.DockBpId, link.LinkId);
            Assert.Equal("ta", link.ForeignTreeId);
            Assert.Equal(CrossTreeDockFixture.DockUT, link.DockUT);
            Assert.Equal(CrossTreeDockFixture.PidB, link.PartnerPid);
            Assert.Equal("B0", link.ClaimedRecordingId);
            Assert.Equal("AB", link.MergedChildRecordingId);
            Assert.Equal(BranchPointType.Dock, link.ClaimType);
            Assert.Equal("Stack AB", link.ForeignVesselName);
        }

        [Fact]
        public void FindLinks_ConclusiveGuidMismatch_Declines()
        {
            // tb's B0 carries a DIFFERENT launch guid than the B the foreign tree recorded
            // (relaunch of the same craft; pid is craft-baked). The claim must not match.
            var tb = CrossTreeDockFixture.PartnerTree(guidB: "cccccccccccccccccccccccccccccccc");
            var ta = CrossTreeDockFixture.ControllerTree();

            var links = MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta });

            Assert.Empty(links);
        }

        [Fact]
        public void FindLinks_ControllerSide_HasNoLinks()
        {
            // The affordance is partner-side only: ta's own dock BP is not a FOREIGN claim on ta.
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();

            var links = MissionCrossTreeDock.FindLinks(ta, new List<RecordingTree> { tb, ta });

            Assert.Empty(links);
        }

        [Fact]
        public void FindLinks_BoardClaim_Offered()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree(claimType: BranchPointType.Board);

            var links = MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta });

            Assert.Equal(BranchPointType.Board, Assert.Single(links).ClaimType);
        }

        [Fact]
        public void FindLinks_ZeroTargetPid_Skipped()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            ta.BranchPoints[0].TargetVesselPersistentId = 0;

            Assert.Empty(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));
        }

        // ---- partner journey walk ----

        private static ForeignDockLink LinkFor(RecordingTree tb, RecordingTree ta)
            => Assert.Single(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));

        [Fact]
        public void Journey_FollowsDockedStretch_ThenPartnerOffshoot()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();

            var journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta));

            Assert.Equal(new[] { "AB", "B1" }, journey);
        }

        [Fact]
        public void Journey_NeverUndocked_FollowsLineToEnd()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree(withUndock: false);

            var journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta));

            Assert.Equal(new[] { "AB" }, journey);
        }

        [Fact]
        public void Journey_PartnerSurvivedAsMergedStack_FollowsContinuingStack()
        {
            // A docked INTO B: the merged stack keeps B's pid/guid; at the undock the
            // pid-preference follows the CONTINUING stack (B), and A departs as the offshoot.
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree(
                mergedPid: CrossTreeDockFixture.PidB, mergedGuid: CrossTreeDockFixture.GuidB);

            var journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta));

            Assert.Equal(new[] { "AB", "B1" }, journey);
        }

        [Fact]
        public void Journey_SpansEnvSplitChain()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            // Split the docked stretch into two chain segments: AB [150..220] idx0 (no BP),
            // AB2 [220..300] idx1 carrying the undock BP.
            var ab = ta.Recordings["AB"];
            ab.ExplicitEndUT = 220;
            ab.ChildBranchPointId = null;
            var ab2 = CrossTreeDockFixture.Rec("AB2", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAB", 1, 220, CrossTreeDockFixture.UndockUT,
                pods: 1, childBp: "undockbp", vessel: "Stack AB");
            ab2.TreeId = ta.Id;
            ta.Recordings["AB2"] = ab2;
            ta.BranchPoints.First(bp => bp.Id == "undockbp").ParentRecordingIds =
                new List<string> { "AB2" };

            var journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta));

            Assert.Equal(new[] { "AB", "AB2", "B1" }, journey);
        }

        // ---- journey windows + selection ----

        [Fact]
        public void JourneyWindows_SharedLineStartsAtDock_OffshootWhole()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var structure = MissionStructureBuilder.Build(ta);
            var view = MissionThroughLineBuilder.Build(structure);
            var journey = new HashSet<string>(
                MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta)),
                StringComparer.Ordinal);

            var windows = MissionCrossTreeDock.ComputeJourneyWindowsByOwner(structure, view, journey);

            // Two owners: A's line (one run = the docked stretch only) and B's offshoot.
            Assert.Equal(2, windows.Count);
            var aRun = Assert.Single(windows["A0"]);
            Assert.Equal(CrossTreeDockFixture.DockUT, aRun.StartUT);
            Assert.Equal(CrossTreeDockFixture.UndockUT, aRun.EndUT);
            var bRun = Assert.Single(windows["B1"]);
            Assert.Equal(CrossTreeDockFixture.UndockUT, bRun.StartUT);
            Assert.Equal(380, bRun.EndUT);
        }

        [Fact]
        public void JourneySelectableKeys_CoverDockedStretchAndOffshoot_NotPreDock()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var structure = MissionStructureBuilder.Build(ta);
            var view = MissionThroughLineBuilder.Build(structure);
            var compRoots = MissionCompositionBuilder.Build(structure);
            var journey = new HashSet<string>(
                MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta)),
                StringComparer.Ordinal);
            var windows = MissionCrossTreeDock.ComputeJourneyWindowsByOwner(structure, view, journey);

            var keys = new HashSet<string>(StringComparer.Ordinal);
            MissionCrossTreeDock.CollectJourneySelectableKeys(compRoots, windows, keys);

            // The docked-stretch sub-interval + B's offshoot branch; never A's pre-dock or
            // post-undock intervals.
            Assert.Equal(2, keys.Count);
            Assert.Contains(keys, k => k.Contains("@dock"));
            Assert.Contains("B1", keys);
            Assert.DoesNotContain("A0", keys);
        }

        [Fact]
        public void IncludedJourneyRenderWindows_ExcludingDockedStretch_KeepsOffshootOnly()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var structure = MissionStructureBuilder.Build(ta);
            var view = MissionThroughLineBuilder.Build(structure);
            var compRoots = MissionCompositionBuilder.Build(structure);
            var journey = new HashSet<string>(
                MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, LinkFor(tb, ta)),
                StringComparer.Ordinal);
            var windows = MissionCrossTreeDock.ComputeJourneyWindowsByOwner(structure, view, journey);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            MissionCrossTreeDock.CollectJourneySelectableKeys(compRoots, windows, keys);
            string dockedKey = keys.First(k => k.Contains("@dock"));

            var included = MissionCrossTreeDock.ComputeIncludedJourneyRenderWindows(
                compRoots, windows, new HashSet<string> { dockedKey });

            Assert.False(included.ContainsKey("A0"));
            Assert.True(included.ContainsKey("B1"));
        }

        // ---- member merge ----

        [Fact]
        public void MergeForeignMemberWindows_AddsDockedStretchAndOffshootMembers()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var indexById = CrossTreeDockFixture.IndexById(committed);
            var mission = new Mission("m1", "tb", "B mission");
            mission.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            var windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>();

            int added = MissionCrossTreeDock.MergeForeignMemberWindows(
                mission, tb, new List<RecordingTree> { tb, ta }, committed, indexById, windows,
                out int resolved, out int stale);

            Assert.Equal(2, added);
            Assert.Equal(1, resolved);
            Assert.Equal(0, stale);
            int abIdx = indexById["AB"];
            int b1Idx = indexById["B1"];
            Assert.Equal(CrossTreeDockFixture.DockUT, windows[abIdx].StartUT);
            Assert.Equal(CrossTreeDockFixture.UndockUT, windows[abIdx].EndUT);
            Assert.Equal(CrossTreeDockFixture.UndockUT, windows[b1Idx].StartUT);
            Assert.Equal(380, windows[b1Idx].EndUT);
            // A's own legs never join.
            Assert.False(windows.ContainsKey(indexById["A0"]));
            Assert.False(windows.ContainsKey(indexById["A1"]));
        }

        [Fact]
        public void MergeForeignMemberWindows_StaleLink_CountsStale_AddsNothing()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var mission = new Mission("m1", "tb", "B mission");
            mission.IncludedForeignDockLinkIds.Add("no-such-bp");
            var windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>();

            int added = MissionCrossTreeDock.MergeForeignMemberWindows(
                mission, tb, new List<RecordingTree> { tb, ta }, committed,
                CrossTreeDockFixture.IndexById(committed), windows,
                out int resolved, out int stale);

            Assert.Equal(0, added);
            Assert.Equal(0, resolved);
            Assert.Equal(1, stale);
            Assert.Empty(windows);
        }

        [Fact]
        public void MergeForeignMemberWindows_DuplicateIndex_FirstClaimantWins()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var indexById = CrossTreeDockFixture.IndexById(committed);
            var mission = new Mission("m1", "tb", "B mission");
            mission.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            var windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                [indexById["AB"]] = new GhostPlaybackLogic.LoopUnit.MemberWindow(1.0, 2.0)
            };

            MissionCrossTreeDock.MergeForeignMemberWindows(
                mission, tb, new List<RecordingTree> { tb, ta }, committed, indexById, windows,
                out int _, out int _);

            Assert.Equal(1.0, windows[indexById["AB"]].StartUT);
            Assert.Equal(2.0, windows[indexById["AB"]].EndUT);
        }

        // ---- loop unit ----

        private static Mission LinkedLoopingMission()
        {
            var mission = new Mission("m1", "tb", "B mission")
            {
                LoopPlayback = true,
                LoopAnchorUT = 1000.0,
            };
            mission.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            return mission;
        }

        [Fact]
        public void LoopUnit_CrossTree_MembersFromBothTrees_ShareOneSpan()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var indexById = CrossTreeDockFixture.IndexById(committed);
            var mission = LinkedLoopingMission();

            var set = MissionLoopUnitBuilder.Build(
                new List<Mission> { mission },
                new List<RecordingTree> { tb, ta },
                committed, 600.0);

            Assert.Equal(1, set.Count);
            var unit = set.UnitsByOwner.Values.Single();
            Assert.Equal(indexById["B0"], unit.OwnerIndex);
            Assert.Contains(indexById["B0"], unit.MemberIndices);
            Assert.Contains(indexById["AB"], unit.MemberIndices);
            Assert.Contains(indexById["B1"], unit.MemberIndices);
            Assert.DoesNotContain(indexById["A0"], unit.MemberIndices);
            Assert.DoesNotContain(indexById["A1"], unit.MemberIndices);
            Assert.Equal(0.0, unit.SpanStartUT);
            Assert.Equal(380.0, unit.SpanEndUT);
        }

        [Fact]
        public void LoopUnit_CrossTree_WithoutLink_KeepsOwnTreeOnly()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var indexById = CrossTreeDockFixture.IndexById(committed);
            var mission = LinkedLoopingMission();
            mission.IncludedForeignDockLinkIds.Clear();

            var set = MissionLoopUnitBuilder.Build(
                new List<Mission> { mission },
                new List<RecordingTree> { tb, ta },
                committed, 600.0);

            var unit = set.UnitsByOwner.Values.Single();
            Assert.Equal(new[] { indexById["B0"] }, unit.MemberIndices);
            Assert.Equal(100.0, unit.SpanEndUT);
        }

        private sealed class InertBodyInfo : IBodyInfo
        {
            public double RotationPeriod(string bodyName) => double.NaN;
            public double OrbitPeriod(string bodyName) => double.NaN;
            public string ReferenceBodyName(string bodyName) => null;
            public double SoiRadius(string bodyName) => double.NaN;
            public double OrbitalVelocity(string bodyName) => double.NaN;
            public double GravParameter(string bodyName) => double.NaN;
            public double Radius(string bodyName) => double.NaN;
            public bool TryGetVesselOrbit(uint vesselPid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            {
                periodSeconds = double.NaN;
                orbitBodyName = null;
                return false;
            }
        }

        [Fact]
        public void LoopUnit_CrossTree_PeriodicityFailsClosed_WithLoggedReason()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var mission = LinkedLoopingMission();

            var set = MissionLoopUnitBuilder.Build(
                new List<Mission> { mission },
                new List<RecordingTree> { tb, ta },
                committed, 600.0, new InertBodyInfo());

            Assert.Equal(1, set.Count);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("cross-tree partner-journey member(s)")
                && l.Contains("fail closed"));
        }

        [Fact]
        public void FindLinks_DebrisPidCollision_Declined()
        {
            // A guid-less DEBRIS recording carrying the claimed pid must not mint a partner
            // journey (pids are craft-baked; the debris is an unrelated launch).
            var tb = CrossTreeDockFixture.Tree("tb", new[]
            {
                CrossTreeDockFixture.Rec("B0", CrossTreeDockFixture.PidB, null,
                    "CB", 0, 0, 100, debris: true),
            });
            var ta = CrossTreeDockFixture.ControllerTree();

            Assert.Empty(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));
        }

        [Fact]
        public void Journey_Redock_SoloStretchIsNotOffered_TwoRunsOnForeignLine()
        {
            // Undock then RE-DOCK: A's line carries two disjoint docked stretches; the solo
            // stretch [300..320] between them is NOT partner journey (per-run windows, not
            // a min/max collapse).
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTreeWithRedock();
            var structure = MissionStructureBuilder.Build(ta);
            var view = MissionThroughLineBuilder.Build(structure);
            var compRoots = MissionCompositionBuilder.Build(structure);
            var link = LinkFor(tb, ta);

            var journeyList = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, link);
            Assert.Equal(new[] { "AB", "B1", "AB2" }, journeyList);

            var journey = new HashSet<string>(journeyList, StringComparer.Ordinal);
            var windows = MissionCrossTreeDock.ComputeJourneyWindowsByOwner(structure, view, journey);
            Assert.Equal(2, windows["A0"].Count);
            Assert.Equal(CrossTreeDockFixture.DockUT, windows["A0"][0].StartUT);
            Assert.Equal(CrossTreeDockFixture.UndockUT, windows["A0"][0].EndUT);
            Assert.Equal(320, windows["A0"][1].StartUT);
            Assert.Equal(400, windows["A0"][1].EndUT);

            // A's solo stretch interval (the structural piece [300..320]) is never offered.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            MissionCrossTreeDock.CollectJourneySelectableKeys(compRoots, windows, keys);
            Assert.DoesNotContain(keys, k => !k.Contains("@dock") && k != "B1");

            // Member merge picks up both docked stretches + B's between-docks leg, never A's.
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var indexById = CrossTreeDockFixture.IndexById(committed);
            var mission = new Mission("m1", "tb", "B mission");
            mission.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            var memberWindows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>();
            MissionCrossTreeDock.MergeForeignMemberWindows(
                mission, tb, new List<RecordingTree> { tb, ta }, committed, indexById,
                memberWindows, out int _, out int _);
            Assert.True(memberWindows.ContainsKey(indexById["AB"]));
            Assert.True(memberWindows.ContainsKey(indexById["B1"]));
            Assert.True(memberWindows.ContainsKey(indexById["AB2"]));
            Assert.False(memberWindows.ContainsKey(indexById["A0"]));
            Assert.False(memberWindows.ContainsKey(indexById["A1"]));
            Assert.Equal(320, memberWindows[indexById["AB2"]].StartUT);
            Assert.Equal(400, memberWindows[indexById["AB2"]].EndUT);
        }

        [Fact]
        public void BuildSignature_FoldsLinkState_AndForeignTreeTopology()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            var committed = CrossTreeDockFixture.Committed(tb, ta);
            var trees = new List<RecordingTree> { tb, ta };
            var mission = LinkedLoopingMission();
            mission.IncludedForeignDockLinkIds.Clear();

            string withoutLink = MissionLoopUnitBuilder.BuildSignature(
                new List<Mission> { mission }, trees, committed, 600.0);
            mission.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            string withLink = MissionLoopUnitBuilder.BuildSignature(
                new List<Mission> { mission }, trees, committed, 600.0);
            Assert.NotEqual(withoutLink, withLink);

            // A FOREIGN-tree topology change must move the linked signature (the journey can
            // change without touching the mission's own tree).
            ta.Recordings["EXTRA"] = CrossTreeDockFixture.Rec(
                "EXTRA", 999, null, "CX", 0, 500, 600);
            string withForeignChange = MissionLoopUnitBuilder.BuildSignature(
                new List<Mission> { mission }, trees, committed, 600.0);
            Assert.NotEqual(withLink, withForeignChange);
        }
    }
}
