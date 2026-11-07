using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct unit tests for the pure decision units of ParsekFlightPlaybackHost: the chain-path
    /// and fallback recording lookups, the playback coverage predicates, the per-frame
    /// spawn-suppression reason chain and its histogram, the ghost skip-reason change logger,
    /// the Re-Fly anchor hold bookkeeping and the reapply trace formatters.
    /// </summary>
    [Collection("Sequential")]
    public class PlaybackHostUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PlaybackHostUnitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static Recording Rec(string id, uint pid = 0, double start = double.NaN, double end = double.NaN,
            string treeId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                TreeId = treeId,
            };
        }

        private static Recording ChainRec(string id, string chainId, int chainIndex, uint pid,
            double start, double end, int branch = 0, string childBp = null)
        {
            var rec = Rec(id, pid, start, end);
            rec.ChainId = chainId;
            rec.ChainIndex = chainIndex;
            rec.ChainBranch = branch;
            rec.ChildBranchPointId = childBp;
            return rec;
        }

        private static RecordingTree Tree(string id, Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree { Id = id };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static BranchPoint Bp(string id, params string[] children)
        {
            return new BranchPoint
            {
                Id = id,
                ParentRecordingIds = new List<string>(),
                ChildRecordingIds = new List<string>(children),
            };
        }

        private static ChainLink Link(string treeId, double ut, string recordingId = null)
        {
            return new ChainLink { treeId = treeId, ut = ut, recordingId = recordingId };
        }

        private static GhostChain Chain(params ChainLink[] links)
        {
            return new GhostChain { Links = new List<ChainLink>(links) };
        }

        private static Recording OrbitRec(string id, uint pid, params double[] bounds)
        {
            var rec = Rec(id, pid);
            for (int i = 0; i + 1 < bounds.Length; i += 2)
                rec.OrbitSegments.Add(new OrbitSegment { startUT = bounds[i], endUT = bounds[i + 1] });
            return rec;
        }

        private static Recording SurfaceRec(string id, uint pid, double start, double end)
        {
            var rec = Rec(id, pid, start, end);
            rec.SurfacePos = new SurfacePosition { body = "Kerbin" };
            return rec;
        }

        // --- FindChainPathRecordingAtUT (tree + start overload) ---

        [Fact]
        public void ChainPath_NullTreeOrStart_ReturnsNull()
        {
            var r = Rec("r1", 7, 0, 10);
            var tree = Tree("t", new[] { r });
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT((RecordingTree)null, r, 7, 5));
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, null, 7, 5));
        }

        [Fact]
        public void ChainPath_StartRecordingCoversUT_ReturnsStart()
        {
            var r = Rec("r1", 7, 0, 10);
            var tree = Tree("t", new[] { r });
            Assert.Same(r, ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r, 7, 5));
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(10.0, true)]
        [InlineData(-0.001, false)]
        [InlineData(10.001, false)]
        public void ChainPath_UtBoundsAreInclusive(double ut, bool found)
        {
            var r = Rec("r1", 7, 0, 10);
            var tree = Tree("t", new[] { r });
            var result = ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r, 7, ut);
            Assert.Equal(found, result != null);
        }

        [Fact]
        public void ChainPath_PidMismatchOnStart_WalksToChainSuccessor()
        {
            var r0 = ChainRec("r0", "c", 0, 9, 0, 10);
            var r1 = ChainRec("r1", "c", 1, 7, 10, 20);
            var tree = Tree("t", new[] { r0, r1 });
            Assert.Same(r1, ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 15));
        }

        [Fact]
        public void ChainPath_FollowsChainIndexAcrossSeveralHops()
        {
            var r0 = ChainRec("r0", "c", 0, 7, 0, 10);
            var r1 = ChainRec("r1", "c", 1, 7, 10, 20);
            var r2 = ChainRec("r2", "c", 2, 7, 20, 30);
            var tree = Tree("t", new[] { r2, r0, r1 });
            Assert.Same(r2, ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 25));
        }

        [Fact]
        public void ChainPath_DoesNotCrossChainBranch()
        {
            var r0 = ChainRec("r0", "c", 0, 7, 0, 10);
            var r1Other = ChainRec("r1b", "c", 1, 7, 10, 20, branch: 1);
            var tree = Tree("t", new[] { r0, r1Other });
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 15));
        }

        [Fact]
        public void ChainPath_ChainSuccessorWinsOverChildBranch()
        {
            var r0 = ChainRec("r0", "c", 0, 7, 0, 10, childBp: "bp");
            var r1 = ChainRec("r1", "c", 1, 7, 10, 20);
            var child = Rec("kid", 7, 10, 20);
            var tree = Tree("t", new[] { r0, r1, child }, new[] { Bp("bp", "kid") });
            Assert.Same(r1, ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 15));
        }

        [Fact]
        public void ChainPath_NoSuccessor_DescendsIntoSamePidChild()
        {
            var r0 = Rec("r0", 7, 0, 10);
            r0.ChildBranchPointId = "bp";
            var debris = Rec("debris", 99, 10, 20);
            var kid = Rec("kid", 7, 10, 20);
            var tree = Tree("t", new[] { r0, debris, kid }, new[] { Bp("bp", "debris", "kid") });
            Assert.Same(kid, ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 15));
        }

        [Fact]
        public void ChainPath_Cycle_TerminatesWithNull()
        {
            var r0 = ChainRec("r0", "c", 0, 9, 0, 10);
            var r1 = ChainRec("r1", "c", 1, 9, 10, 20, childBp: "bp");
            var tree = Tree("t", new[] { r0, r1 }, new[] { Bp("bp", "r0") });
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 5));
        }

        [Fact]
        public void ChainPath_DeadEnd_ReturnsNull()
        {
            var r0 = Rec("r0", 7, 0, 10);
            var tree = Tree("t", new[] { r0 });
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(tree, r0, 7, 50));
        }

        // --- FindChainPathRecordingAtUT (committed trees + link overload) ---

        [Fact]
        public void ChainPathByLink_NullTreesOrEmptyTreeId_ReturnsNull()
        {
            var tree = Tree("t", new[] { Rec("r0", 7, 0, 10) });
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(
                (IReadOnlyList<RecordingTree>)null, Link("t", 0, "r0"), 7, 5));
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(
                new List<RecordingTree> { tree }, Link("", 0, "r0"), 7, 5));
        }

        [Fact]
        public void ChainPathByLink_UnknownTreeOrRecording_ReturnsNull()
        {
            var trees = new List<RecordingTree> { Tree("t", new[] { Rec("r0", 7, 0, 10) }) };
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(trees, Link("other", 0, "r0"), 7, 5));
            Assert.Null(ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(trees, Link("t", 0, "missing"), 7, 5));
        }

        [Fact]
        public void ChainPathByLink_ResolvesStartFromLinkRecording()
        {
            var r0 = ChainRec("r0", "c", 0, 7, 0, 10);
            var r1 = ChainRec("r1", "c", 1, 7, 10, 20);
            var trees = new List<RecordingTree>
            {
                Tree("a", new[] { Rec("x", 7, 0, 100) }),
                Tree("t", new[] { r0, r1 }),
            };
            Assert.Same(r1, ParsekFlightPlaybackHost.FindChainPathRecordingAtUT(trees, Link("t", 0, "r0"), 7, 15));
        }

        // --- FindNextChainRecording / FindPreferredChildRecording ---

        [Fact]
        public void NextChain_NullOrUnchained_ReturnsNull()
        {
            var r0 = Rec("r0");
            var tree = Tree("t", new[] { r0 });
            Assert.Null(ParsekFlightPlaybackHost.FindNextChainRecording(null, r0));
            Assert.Null(ParsekFlightPlaybackHost.FindNextChainRecording(tree, null));
            Assert.Null(ParsekFlightPlaybackHost.FindNextChainRecording(tree, r0));
        }

        [Fact]
        public void NextChain_MatchesChainIdIndexAndBranch()
        {
            var r0 = ChainRec("r0", "c", 0, 7, 0, 10, branch: 1);
            var wrongChain = ChainRec("w", "d", 1, 7, 10, 20, branch: 1);
            var wrongIndex = ChainRec("i", "c", 2, 7, 10, 20, branch: 1);
            var wrongBranch = ChainRec("b", "c", 1, 7, 10, 20, branch: 0);
            var right = ChainRec("r1", "c", 1, 7, 10, 20, branch: 1);
            var tree = Tree("t", new[] { r0, wrongChain, wrongIndex, wrongBranch, right });
            Assert.Same(right, ParsekFlightPlaybackHost.FindNextChainRecording(tree, r0));
        }

        [Fact]
        public void PreferredChild_NoChildBranchPoint_ReturnsNull()
        {
            var r0 = Rec("r0", 7);
            Assert.Null(ParsekFlightPlaybackHost.FindPreferredChildRecording(Tree("t", new[] { r0 }), r0));
            Assert.Null(ParsekFlightPlaybackHost.FindPreferredChildRecording(null, r0));
        }

        [Fact]
        public void PreferredChild_BranchPointMissingOrEmpty_ReturnsNull()
        {
            var r0 = Rec("r0", 7);
            r0.ChildBranchPointId = "bp";
            Assert.Null(ParsekFlightPlaybackHost.FindPreferredChildRecording(Tree("t", new[] { r0 }), r0));
            Assert.Null(ParsekFlightPlaybackHost.FindPreferredChildRecording(
                Tree("t", new[] { r0 }, new[] { Bp("bp") }), r0));
        }

        [Fact]
        public void PreferredChild_PrefersSamePidChild()
        {
            var r0 = Rec("r0", 7);
            r0.ChildBranchPointId = "bp";
            var a = Rec("a", 1);
            var b = Rec("b", 7);
            var tree = Tree("t", new[] { r0, a, b }, new[] { Bp("bp", "a", "b") });
            Assert.Same(b, ParsekFlightPlaybackHost.FindPreferredChildRecording(tree, r0));
        }

        [Fact]
        public void PreferredChild_NoSamePidChild_FallsBackToFirst()
        {
            var r0 = Rec("r0", 7);
            r0.ChildBranchPointId = "bp";
            var a = Rec("a", 1);
            var b = Rec("b", 2);
            var tree = Tree("t", new[] { r0, a, b }, new[] { Bp("bp", "a", "b") });
            Assert.Same(a, ParsekFlightPlaybackHost.FindPreferredChildRecording(tree, r0));
        }

        [Fact]
        public void PreferredChild_ZeroPidParent_TakesFirstEvenWhenAChildHasPidZero()
        {
            var r0 = Rec("r0", 0);
            r0.ChildBranchPointId = "bp";
            var a = Rec("a", 5);
            var b = Rec("b", 0);
            var tree = Tree("t", new[] { r0, a, b }, new[] { Bp("bp", "a", "b") });
            Assert.Same(a, ParsekFlightPlaybackHost.FindPreferredChildRecording(tree, r0));
        }

        [Fact]
        public void PreferredChild_ChosenIdNotInTree_ReturnsNull()
        {
            var r0 = Rec("r0", 7);
            r0.ChildBranchPointId = "bp";
            var tree = Tree("t", new[] { r0 }, new[] { Bp("bp", "ghost-id") });
            Assert.Null(ParsekFlightPlaybackHost.FindPreferredChildRecording(tree, r0));
        }

        // --- GetFirstClaimTreeIds / ChainHasStarted ---

        [Fact]
        public void FirstClaimTreeIds_NullOrEmptyChain_ReturnsEmptySet()
        {
            Assert.Empty(ParsekFlightPlaybackHost.GetFirstClaimTreeIds(null));
            Assert.Empty(ParsekFlightPlaybackHost.GetFirstClaimTreeIds(new GhostChain()));
            Assert.Empty(ParsekFlightPlaybackHost.GetFirstClaimTreeIds(Chain()));
        }

        [Fact]
        public void FirstClaimTreeIds_CollectsTreesAtTheEarliestUtOnly()
        {
            var chain = Chain(
                Link("late", 200),
                Link("early-a", 100),
                Link("early-b", 100.0005),
                Link("early-a", 100),
                Link("just-out", 100.002));
            var ids = ParsekFlightPlaybackHost.GetFirstClaimTreeIds(chain);
            Assert.Equal(new HashSet<string> { "early-a", "early-b" }, ids);
        }

        [Fact]
        public void FirstClaimTreeIds_EarliestLinkWithoutTreeId_IsSkipped()
        {
            var chain = Chain(Link(null, 50), Link("", 50), Link("t", 60));
            Assert.Empty(ParsekFlightPlaybackHost.GetFirstClaimTreeIds(chain));
        }

        [Fact]
        public void FirstClaimTreeIds_AllLinksAtMaxValue_ReturnsEmpty()
        {
            var chain = Chain(Link("t", double.MaxValue));
            Assert.Empty(ParsekFlightPlaybackHost.GetFirstClaimTreeIds(chain));
        }

        [Fact]
        public void ChainHasStarted_NullOrNoLinks_False()
        {
            Assert.False(ParsekFlightPlaybackHost.ChainHasStarted(null, 100));
            Assert.False(ParsekFlightPlaybackHost.ChainHasStarted(new GhostChain(), 100));
            Assert.False(ParsekFlightPlaybackHost.ChainHasStarted(Chain(), 100));
        }

        [Theory]
        [InlineData(99.0, true)]
        [InlineData(100.0, true)]
        [InlineData(100.0005, true)]
        [InlineData(100.01, false)]
        public void ChainHasStarted_AnyLinkAtOrBeforeUtWithinTolerance(double firstLinkUT, bool expected)
        {
            var chain = Chain(Link("t", 500), Link("t", firstLinkUT));
            Assert.Equal(expected, ParsekFlightPlaybackHost.ChainHasStarted(chain, 100));
        }

        // --- coverage predicates ---

        [Fact]
        public void OrbitCoverage_InsideAnySegmentInclusive()
        {
            var rec = OrbitRec("r", 7, 10, 20, 30, 40);
            Assert.True(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(rec, 10));
            Assert.True(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(rec, 20));
            Assert.True(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(rec, 35));
            Assert.False(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(rec, 25));
            Assert.False(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(rec, 41));
        }

        [Fact]
        public void OrbitCoverage_NullOrNoSegments_False()
        {
            Assert.False(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(null, 10));
            Assert.False(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(Rec("r"), 10));
            var nullSegments = Rec("r");
            nullSegments.OrbitSegments = null;
            Assert.False(ParsekFlightPlaybackHost.HasOrbitCoverageAtUT(nullSegments, 10));
        }

        [Theory]
        [InlineData(10.0, true)]
        [InlineData(15.0, true)]
        [InlineData(20.0, true)]
        [InlineData(9.9, false)]
        [InlineData(20.1, false)]
        public void SurfaceCoverage_InsideRecordingWindowInclusive(double ut, bool expected)
        {
            Assert.Equal(expected, ParsekFlightPlaybackHost.HasSurfaceCoverageAtUT(SurfaceRec("r", 7, 10, 20), ut));
        }

        [Fact]
        public void SurfaceCoverage_NoSurfacePos_False()
        {
            Assert.False(ParsekFlightPlaybackHost.HasSurfaceCoverageAtUT(Rec("r", 7, 10, 20), 15));
            Assert.False(ParsekFlightPlaybackHost.HasSurfaceCoverageAtUT(null, 15));
        }

        [Fact]
        public void UsablePlaybackData_PointsCountAtAnyUt()
        {
            var rec = Rec("r");
            rec.Points.Add(new TrajectoryPoint { ut = 10 });
            Assert.True(ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(rec, 9999));
            Assert.False(ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(rec, 9999));
        }

        [Fact]
        public void UsablePlaybackData_OrbitOrSurfaceCoverage()
        {
            var orbit = OrbitRec("o", 7, 10, 20);
            var surface = SurfaceRec("s", 7, 10, 20);
            Assert.True(ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(orbit, 15));
            Assert.True(ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(orbit, 15));
            Assert.True(ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(surface, 15));
            Assert.True(ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(surface, 15));
            Assert.False(ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(orbit, 25));
            Assert.False(ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(surface, 25));
        }

        [Fact]
        public void UsablePlaybackData_NullOrEmpty_False()
        {
            Assert.False(ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(null, 1));
            Assert.False(ParsekFlightPlaybackHost.RecordingHasUsableNonPointPlaybackDataAtUT(null, 1));
            Assert.False(ParsekFlightPlaybackHost.RecordingHasUsablePlaybackDataAtUT(Rec("r", 7, 0, 10), 5));
        }

        // --- FindPlaybackFallbackRecordingForVessel ---

        [Fact]
        public void Fallback_NullList_ReturnsNull()
        {
            Assert.Null(ParsekFlightPlaybackHost.FindPlaybackFallbackRecordingForVessel(null, 7, 15));
        }

        [Fact]
        public void Fallback_ReturnsFirstCoveringRecordingForPid()
        {
            var otherPid = OrbitRec("other", 8, 0, 100);
            var uncovered = OrbitRec("uncovered", 7, 50, 60);
            var pointsOnly = Rec("points", 7);
            pointsOnly.Points.Add(new TrajectoryPoint { ut = 15 });
            var first = SurfaceRec("first", 7, 10, 20);
            var second = OrbitRec("second", 7, 0, 100);
            var list = new List<Recording> { otherPid, uncovered, pointsOnly, first, second };
            Assert.Same(first, ParsekFlightPlaybackHost.FindPlaybackFallbackRecordingForVessel(list, 7, 15));
        }

        [Fact]
        public void Fallback_ExcludedTreeIsSkipped_EmptyTreeIdIsNotExcludable()
        {
            var excluded = OrbitRec("excluded", 7, 0, 100);
            excluded.TreeId = "tree-x";
            var noTree = OrbitRec("no-tree", 7, 0, 100);
            var list = new List<Recording> { excluded, noTree };
            var excludedIds = new HashSet<string> { "tree-x", "" };
            Assert.Same(noTree, ParsekFlightPlaybackHost.FindPlaybackFallbackRecordingForVessel(list, 7, 15, excludedIds));
            Assert.Same(excluded, ParsekFlightPlaybackHost.FindPlaybackFallbackRecordingForVessel(list, 7, 15));
        }

        // --- spawn suppression reason chain ---

        private const string SpawnGate = "spawn-gate";
        private const string ChainGate = "chain-gate";

        [Theory]
        // liveFlown, historical, spawnNeeds, chainSuppressed, spawnHistorical, expected
        [InlineData(true, true, false, true, true, "live")]
        [InlineData(false, true, false, true, true, "historical-never-replayed")]
        [InlineData(false, false, false, true, true, SpawnGate)]
        [InlineData(false, false, true, true, true, ChainGate)]
        [InlineData(false, false, true, false, true, "historical-never-replayed")]
        [InlineData(false, false, true, false, false, ChainGate)]
        public void SpawnSuppressedReason_ChainOrder(
            bool liveFlown, bool historical, bool spawnNeeds, bool chainSuppressed, bool spawnHistorical,
            string expected)
        {
            string reason = ParsekFlightPlaybackHost.ResolveSpawnSuppressedReason(
                liveFlown, historical, spawnNeeds, SpawnGate, chainSuppressed, ChainGate, spawnHistorical);
            if (expected == "live")
                expected = GhostPlaybackLogic.LiveFlownRecordingSpawnReason;
            Assert.Equal(expected, reason);
        }

        [Fact]
        public void SpawnSuppressedReason_FallthroughCarriesAnEmptyChainReason()
        {
            Assert.Equal("", ParsekFlightPlaybackHost.ResolveSpawnSuppressedReason(
                false, false, true, SpawnGate, false, "", false));
        }

        [Fact]
        public void TallySpawnSuppressedReason_CountsPerKeyAndMapsEmptyToNone()
        {
            var hist = new Dictionary<string, int>();
            ParsekFlightPlaybackHost.TallySpawnSuppressedReason(hist, "a");
            ParsekFlightPlaybackHost.TallySpawnSuppressedReason(hist, "a");
            ParsekFlightPlaybackHost.TallySpawnSuppressedReason(hist, null);
            ParsekFlightPlaybackHost.TallySpawnSuppressedReason(hist, "");
            Assert.Equal(2, hist.Count);
            Assert.Equal(2, hist["a"]);
            Assert.Equal(2, hist["(none)"]);
        }

        // --- Step-2 live-anchor formatters ---

        [Fact]
        public void Step2LiveBind_KeyAndMessage()
        {
            Assert.Equal("step2-live-bind-suppress|rec-1",
                ParsekFlightPlaybackHost.BuildStep2LiveBindSuppressKey("rec-1"));
            Assert.Equal("step2-live-bind-suppress|(none)",
                ParsekFlightPlaybackHost.BuildStep2LiveBindSuppressKey(null));
            Assert.Equal(
                "Step-2 suppressed live-anchor double (live-bind event): rec=rec-1 vessel=\"Station\""
                    + " liveBoundThisOrLastFrame=true"
                    + " (own ghost hidden while a delivery member is docking its launch-matched live vessel)",
                ParsekFlightPlaybackHost.BuildStep2LiveBindSuppressMessage("rec-1", "Station"));
        }

        [Fact]
        public void Step2LiveBind_MessageNullPlaceholders()
        {
            string msg = ParsekFlightPlaybackHost.BuildStep2LiveBindSuppressMessage(null, null);
            Assert.StartsWith("Step-2 suppressed live-anchor double (live-bind event): rec=(none) vessel=\"(none)\"", msg);
        }

        [Fact]
        public void Step2LiveAnchorDoubleSummary_Text()
        {
            Assert.Equal("Step-2 live-anchor-double: suppressed=12 this frame (gate=live-bind-event)",
                ParsekFlightPlaybackHost.BuildStep2LiveAnchorDoubleSummary(12));
        }

        // --- ghost skip-reason logging ---

        [Fact]
        public void SkipReasonIdentity_PrefersRecordingId()
        {
            Assert.Equal("rec-9", ParsekFlightPlaybackHost.BuildGhostSkipReasonIdentity(3, "rec-9"));
            Assert.Equal("idx-3", ParsekFlightPlaybackHost.BuildGhostSkipReasonIdentity(3, null));
            Assert.Equal("idx-0", ParsekFlightPlaybackHost.BuildGhostSkipReasonIdentity(0, ""));
        }

        [Fact]
        public void SkipReasonMessage_Format()
        {
            string msg = ParsekFlightPlaybackHost.BuildGhostSkipReasonMessage(
                4, null, null, GhostPlaybackSkipReason.PlaybackDisabled,
                hasRenderableData: true, playbackEnabled: false, externalVesselSuppressed: false,
                supersededByRelation: true, rewindRetired: false);
            Assert.Equal(
                "Ghost playback skip state: #4 id=(none) vessel=\"?\" skip=True reason=playback-disabled "
                    + "hasRenderableData=True playbackEnabled=False externalVesselSuppressed=False "
                    + "supersededByRelation=True rewindRetired=False",
                msg);
        }

        [Fact]
        public void SkipReasonChange_NoneWithoutPriorSkip_LogsNothing()
        {
            var active = new HashSet<string>();
            ParsekFlightPlaybackHost.LogGhostSkipReasonChangeIfNeededCore(
                active, 0, Rec("r1"), GhostPlaybackSkipReason.None, true, false, false, false);
            Assert.Empty(active);
            Assert.DoesNotContain(logLines, l => l.Contains("Ghost playback skip state"));
        }

        [Fact]
        public void SkipReasonChange_SkipThenClear_LogsBothTransitionsAndTracksIdentity()
        {
            var active = new HashSet<string>();
            var rec = Rec("r1");
            rec.VesselName = "Probe";

            ParsekFlightPlaybackHost.LogGhostSkipReasonChangeIfNeededCore(
                active, 2, rec, GhostPlaybackSkipReason.NoRenderableData, false, false, false, false);
            Assert.Contains("r1", active);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Ghost playback skip state: #2 id=r1 vessel=\"Probe\" skip=True reason=no-renderable-data")
                && l.Contains("playbackEnabled=True"));

            int before = logLines.Count;
            ParsekFlightPlaybackHost.LogGhostSkipReasonChangeIfNeededCore(
                active, 2, rec, GhostPlaybackSkipReason.NoRenderableData, false, false, false, false);
            Assert.Equal(before, logLines.Count);
            Assert.Contains("r1", active);

            ParsekFlightPlaybackHost.LogGhostSkipReasonChangeIfNeededCore(
                active, 2, rec, GhostPlaybackSkipReason.None, true, false, false, false);
            Assert.Empty(active);
            Assert.Contains(logLines, l => l.Contains("id=r1") && l.Contains("skip=False reason=none")
                && l.Contains("suppressed=1"));
        }

        [Fact]
        public void SkipReasonChange_NullRecording_UsesIndexIdentityAndPlaceholders()
        {
            var active = new HashSet<string>();
            ParsekFlightPlaybackHost.LogGhostSkipReasonChangeIfNeededCore(
                active, 5, null, GhostPlaybackSkipReason.ExternalVesselSuppressed, true, true, false, false);
            Assert.Contains("idx-5", active);
            Assert.Contains(logLines, l => l.Contains("#5 id=(none) vessel=\"?\"")
                && l.Contains("reason=external-vessel-suppressed") && l.Contains("playbackEnabled=False")
                && l.Contains("externalVesselSuppressed=True"));
        }

        // --- Re-Fly anchor hold bookkeeping ---

        [Fact]
        public void HoldCandidate_EmptyAnchor_IsIgnored()
        {
            var counts = new Dictionary<string, int>();
            var reasons = new Dictionary<string, string>();
            ParsekFlightPlaybackHost.RecordReFlyAnchorHoldCandidateCore(counts, reasons, null, "x");
            ParsekFlightPlaybackHost.RecordReFlyAnchorHoldCandidateCore(counts, reasons, "", "x");
            Assert.Empty(counts);
            Assert.Empty(reasons);
        }

        [Fact]
        public void HoldCandidate_CountsGhostsAndKeepsTheFirstReason()
        {
            var counts = new Dictionary<string, int>();
            var reasons = new Dictionary<string, string>();
            ParsekFlightPlaybackHost.RecordReFlyAnchorHoldCandidateCore(counts, reasons, "a", "settle-active");
            ParsekFlightPlaybackHost.RecordReFlyAnchorHoldCandidateCore(counts, reasons, "a", "other");
            ParsekFlightPlaybackHost.RecordReFlyAnchorHoldCandidateCore(counts, reasons, "b", null);
            Assert.Equal(2, counts["a"]);
            Assert.Equal(1, counts["b"]);
            Assert.Equal("settle-active", reasons["a"]);
            Assert.Equal("(none)", reasons["b"]);
        }

        [Fact]
        public void HoldTransitions_EngageOnceThenRelease()
        {
            var counts = new Dictionary<string, int> { { "anchor-recording-1", 3 } };
            var reasons = new Dictionary<string, string> { { "anchor-recording-1", "settle-active" } };
            var start = new Dictionary<string, int>();
            var scratch = new List<string>();

            ParsekFlightPlaybackHost.LogReFlyAnchorHoldTransitionsCore(counts, reasons, start, scratch, 100);
            Assert.Equal(100, start["anchor-recording-1"]);
            Assert.Contains(logLines, l => l.Contains("[ReFlySettle]")
                && l.Contains("Anchor ghost visibility hold engaged: anchorRec=anchor-r ghosts=3 reason=settle-active frame=100"));

            int engagedLines = logLines.FindAll(l => l.Contains("hold engaged")).Count;
            ParsekFlightPlaybackHost.LogReFlyAnchorHoldTransitionsCore(counts, reasons, start, scratch, 101);
            Assert.Equal(engagedLines, logLines.FindAll(l => l.Contains("hold engaged")).Count);
            Assert.Equal(100, start["anchor-recording-1"]);

            counts.Clear();
            ParsekFlightPlaybackHost.LogReFlyAnchorHoldTransitionsCore(counts, reasons, start, scratch, 110);
            Assert.Empty(start);
            Assert.Empty(scratch);
            Assert.Contains(logLines, l => l.Contains("Anchor ghost visibility hold released: anchorRec=anchor-r heldFrames=10 frame=110"));
        }

        [Fact]
        public void HoldTransitions_MissingReasonLogsNone_AndHeldFramesNeverNegative()
        {
            var counts = new Dictionary<string, int> { { "a", 1 } };
            var reasons = new Dictionary<string, string>();
            var start = new Dictionary<string, int>();
            var scratch = new List<string> { "stale" };

            ParsekFlightPlaybackHost.LogReFlyAnchorHoldTransitionsCore(counts, reasons, start, scratch, 50);
            Assert.Contains(logLines, l => l.Contains("hold engaged: anchorRec=a ghosts=1 reason=(none) frame=50"));
            Assert.Empty(scratch);

            counts.Clear();
            ParsekFlightPlaybackHost.LogReFlyAnchorHoldTransitionsCore(counts, reasons, start, scratch, 40);
            Assert.Contains(logLines, l => l.Contains("hold released: anchorRec=a heldFrames=0 frame=40"));
        }

        [Fact]
        public void HoldTransitions_OnlyUncountedAnchorsRelease()
        {
            var counts = new Dictionary<string, int> { { "kept", 1 } };
            var reasons = new Dictionary<string, string>();
            var start = new Dictionary<string, int> { { "kept", 5 }, { "gone", 6 } };
            var scratch = new List<string>();

            ParsekFlightPlaybackHost.LogReFlyAnchorHoldTransitionsCore(counts, reasons, start, scratch, 9);
            Assert.True(start.ContainsKey("kept"));
            Assert.False(start.ContainsKey("gone"));
            Assert.DoesNotContain(logLines, l => l.Contains("hold engaged"));
            Assert.Single(logLines.FindAll(l => l.Contains("hold released: anchorRec=gone heldFrames=3 frame=9")));
        }

        [Fact]
        public void AnchorReFlyUnstableFlag_AnyFlagSet()
        {
            Assert.False(ParsekFlightPlaybackHost.HasAnchorReFlyUnstableFlag(null));
            Assert.False(ParsekFlightPlaybackHost.HasAnchorReFlyUnstableFlag(new TrajectoryPlaybackFlags[0]));
            var flags = new TrajectoryPlaybackFlags[3];
            Assert.False(ParsekFlightPlaybackHost.HasAnchorReFlyUnstableFlag(flags));
            flags[2].anchorReFlyUnstable = true;
            Assert.True(ParsekFlightPlaybackHost.HasAnchorReFlyUnstableFlag(flags));
        }

        // --- reapply trace helpers ---

        [Fact]
        public void GhostPosEntryRecordingId_Precedence()
        {
            var e = new ParsekFlight.GhostPosEntry
            {
                recordingId = "own",
                anchorRecordingId = "anchor",
                relativeAnchorRecordingId = "relative",
            };
            Assert.Equal("own", ParsekFlightPlaybackHost.ResolveGhostPosEntryRecordingId(e));
            e.recordingId = "";
            Assert.Equal("anchor", ParsekFlightPlaybackHost.ResolveGhostPosEntryRecordingId(e));
            e.anchorRecordingId = null;
            Assert.Equal("relative", ParsekFlightPlaybackHost.ResolveGhostPosEntryRecordingId(e));
            e.relativeAnchorRecordingId = "";
            Assert.Null(ParsekFlightPlaybackHost.ResolveGhostPosEntryRecordingId(e));
        }

        [Fact]
        public void CameraPreCullReapplyMessage_Format()
        {
            string msg = ParsekFlightPlaybackHost.BuildCameraPreCullReapplyMessage(
                "abcd1234", ParsekFlight.GhostPosMode.Relative, 123.45678, 2.5,
                new Vector3d(1, 2.5, -3), new Vector3d(4.004, 5, 6));
            Assert.Equal(
                "Ghost camera pre-cull reapply: rec=abcd1234 mode=Relative ut=123.457 deltaMeters=2.50 "
                    + "before=(1.00,2.50,-3.00) after=(4.00,5.00,6.00)",
                msg);
        }

        [Fact]
        public void SkippedNonReFlyCameraPreCull_LogsOnlyAPositiveCount()
        {
            ParsekFlightPlaybackHost.LogSkippedNonReFlyCameraPreCull("reapply", 0);
            Assert.DoesNotContain(logLines, l => l.Contains("Ghost camera pre-cull skipped"));
            ParsekFlightPlaybackHost.LogSkippedNonReFlyCameraPreCull("reapply", 4);
            Assert.Contains(logLines, l => l.Contains("[Playback]")
                && l.Contains("Ghost camera pre-cull skipped non-Re-Fly reapply entries: count=4"));
        }
    }
}
