using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// IN-FLIGHT-COMMIT-SKIPS-OPTIMIZATION-PASS: <c>ParsekFlight.CommitTreeFlight</c> now runs
    /// <c>RecordingStore.RunOptimizationPass</c> like <c>MergeDialog.MergeCommit</c>. These
    /// cells pin the pure decisions it uses (skip guards, post-pass chain-tip resolution, the
    /// active vessel's spawn-stamp carry) and, source-gated, the call's position in the commit.
    /// </summary>
    [Collection("Sequential")]
    public class InFlightCommitOptimizationTests : IDisposable
    {
        public InFlightCommitOptimizationTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        // --- DecideSkipReason ---

        [Fact]
        public void DecideSkipReason_NoSessionNoJournal_Runs()
        {
            Assert.Null(InFlightCommitOptimization.DecideSkipReason(false, false));
        }

        [Fact]
        public void DecideSkipReason_ReFlySession_Skips()
        {
            Assert.Equal(InFlightCommitOptimization.SkipReasonReFlySession,
                InFlightCommitOptimization.DecideSkipReason(true, false));
            Assert.Equal(InFlightCommitOptimization.SkipReasonReFlySession,
                InFlightCommitOptimization.DecideSkipReason(true, true));
        }

        [Fact]
        public void DecideSkipReason_MergeJournal_Skips()
        {
            Assert.Equal(InFlightCommitOptimization.SkipReasonMergeJournal,
                InFlightCommitOptimization.DecideSkipReason(false, true));
        }

        // --- ResolveChainTip ---

        private static Recording Seg(string id, string tree, string chain, int index, int branch = 0)
        {
            return new Recording
            {
                RecordingId = id, TreeId = tree, ChainId = chain,
                ChainIndex = index, ChainBranch = branch,
            };
        }

        [Fact]
        public void ResolveChainTip_NoChain_ReturnsSelf()
        {
            var rec = Seg("a", "t", null, -1);
            Assert.Same(rec, InFlightCommitOptimization.ResolveChainTip(
                rec, new List<Recording> { rec, Seg("b", "t", "c", 5) }));
        }

        [Fact]
        public void ResolveChainTip_Null_ReturnsNull()
        {
            Assert.Null(InFlightCommitOptimization.ResolveChainTip(null, new List<Recording>()));
        }

        [Fact]
        public void ResolveChainTip_PicksHighestIndexSameTreeBranchZero()
        {
            var head = Seg("head", "t", "c", 0);
            var tip = Seg("tip", "t", "c", 1);
            var otherTree = Seg("other-tree", "t2", "c", 9);
            var parallel = Seg("parallel", "t", "c", 7, branch: 1);
            var committed = new List<Recording> { head, otherTree, parallel, tip };
            Assert.Same(tip, InFlightCommitOptimization.ResolveChainTip(head, committed));
        }

        [Fact]
        public void ResolveChainTip_AbsorbedRecording_ResolvesToMergeTarget()
        {
            // A merge removed "absorbed" from the committed list; the earlier segment took
            // its terminal and is now the chain tip.
            var target = Seg("target", "t", "c", 0);
            var absorbed = Seg("absorbed", "t", "c", 1);
            Assert.Same(target, InFlightCommitOptimization.ResolveChainTip(
                absorbed, new List<Recording> { target }));
        }

        [Fact]
        public void ResolveChainTip_ParallelBranch_ReturnsSelf()
        {
            var rec = Seg("p", "t", "c", 0, branch: 2);
            Assert.Same(rec, InFlightCommitOptimization.ResolveChainTip(
                rec, new List<Recording> { rec, Seg("x", "t", "c", 3) }));
        }

        // --- CarrySpawnStampToTip ---

        [Fact]
        public void CarrySpawnStampToTip_MovesStampAndClearsHead()
        {
            var head = Seg("head", "t", "c", 0);
            head.VesselSpawned = true;
            head.SpawnedVesselPersistentId = 4242;
            var tip = Seg("tip", "t", "c", 1);
            tip.Points.Add(new TrajectoryPoint { ut = 1 });
            tip.Points.Add(new TrajectoryPoint { ut = 2 });

            Assert.True(InFlightCommitOptimization.CarrySpawnStampToTip(head, tip));
            Assert.True(tip.VesselSpawned);
            Assert.Equal(4242u, tip.SpawnedVesselPersistentId);
            Assert.Equal(1, tip.LastAppliedResourceIndex);
            Assert.False(head.VesselSpawned);
            Assert.Equal(0u, head.SpawnedVesselPersistentId);
        }

        [Fact]
        public void CarrySpawnStampToTip_SameRecordingOrUnstamped_NoOp()
        {
            var rec = Seg("r", "t", "c", 0);
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = 7;
            Assert.False(InFlightCommitOptimization.CarrySpawnStampToTip(rec, rec));
            Assert.True(rec.VesselSpawned);

            var unstamped = Seg("u", "t", "c", 0);
            var tip = Seg("v", "t", "c", 1);
            Assert.False(InFlightCommitOptimization.CarrySpawnStampToTip(unstamped, tip));
            Assert.False(tip.VesselSpawned);
        }

        // --- CountSpawnedLeafTips ---

        [Fact]
        public void CountSpawnedLeafTips_CountsDistinctSpawnedTipsExcludingActive()
        {
            var activeHead = Seg("active-head", "t", "ca", 0);
            var activeTip = Seg("active-tip", "t", "ca", 1);
            activeTip.VesselSpawned = true;
            var leafHead = Seg("leaf-head", "t", "cl", 0);
            var leafTip = Seg("leaf-tip", "t", "cl", 1);
            leafTip.VesselSpawned = true;
            var unspawned = Seg("unspawned", "t", null, -1);
            var committed = new List<Recording> { activeHead, activeTip, leafHead, leafTip, unspawned };

            // Pre-commit leaves were the pre-split heads; a duplicate entry counts once.
            var leaves = new List<Recording> { activeHead, leafHead, leafHead, unspawned };
            Assert.Equal(1, InFlightCommitOptimization.CountSpawnedLeafTips(
                leaves, committed, "active-tip"));
        }

        // --- RunPassAndResolveActiveTip: a throw never stops the commit ---

        private readonly List<string> logLines = new List<string>();

        private void CaptureLog()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        [Fact]
        public void RunPassAndResolveActiveTip_NoThrow_ReturnsResolvedTip()
        {
            CaptureLog();
            bool ran = false;
            string id = InFlightCommitOptimization.RunPassAndResolveActiveTip(
                () => ran = true, () => "tip", "active",
                out bool passThrew, out bool stampMoveThrew);
            Assert.True(ran);
            Assert.Equal("tip", id);
            Assert.False(passThrew);
            Assert.False(stampMoveThrew);
            Assert.DoesNotContain(logLines, l => l.Contains("threw"));
        }

        [Fact]
        public void RunPassAndResolveActiveTip_PassThrows_LogsErrorAndStillMovesStamp()
        {
            CaptureLog();
            bool resolved = false;
            string id = InFlightCommitOptimization.RunPassAndResolveActiveTip(
                () => throw new System.IO.IOException("sidecar locked"),
                () => { resolved = true; return "tip"; },
                "active",
                out bool passThrew, out bool stampMoveThrew);
            Assert.True(passThrew);
            Assert.False(stampMoveThrew);
            Assert.True(resolved);
            Assert.Equal("tip", id);
            Assert.Contains(logLines, l => l.Contains("[ERROR]") && l.Contains("[Flight]")
                && l.Contains(InFlightCommitOptimization.PassThrewLogToken)
                && l.Contains("IOException") && l.Contains("sidecar locked"));
        }

        [Fact]
        public void RunPassAndResolveActiveTip_StampMoveThrows_FallsBackToActiveId()
        {
            CaptureLog();
            string id = InFlightCommitOptimization.RunPassAndResolveActiveTip(
                () => { },
                () => throw new InvalidOperationException("bad chain"),
                "active",
                out bool passThrew, out bool stampMoveThrew);
            Assert.False(passThrew);
            Assert.True(stampMoveThrew);
            Assert.Equal("active", id);
            Assert.Contains(logLines, l => l.Contains("[ERROR]")
                && l.Contains(InFlightCommitOptimization.StampMoveThrewLogToken)
                && l.Contains("activeRec=active"));
        }

        [Fact]
        public void RunPassAndResolveActiveTip_NullTip_FallsBackToActiveId()
        {
            Assert.Equal("active", InFlightCommitOptimization.RunPassAndResolveActiveTip(
                () => { }, () => null, "active", out _, out _));
        }

        // --- Integration over the real optimizer ---

        private static Recording MakeActiveSplittableRecording(RecordingTree tree)
        {
            var rec = new Recording
            {
                RecordingId = "active-rec",
                VesselName = "Lander",
                TreeId = tree.Id,
                VesselPersistentId = 42,
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                VesselSnapshot = new ConfigNode("VESSEL"),
                RecordingFormatVersion = 0,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 42,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 80000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 40000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 30000, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 17060, altitude = 100, bodyName = "Kerbin" });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });
            tree.Recordings[rec.RecordingId] = rec;
            tree.RootRecordingId = rec.RecordingId;
            return rec;
        }

        [Fact]
        public void OptimizerSplitOfStampedActiveRecording_StampFollowsVesselToTip()
        {
            var tree = new RecordingTree { Id = "tree-inflight", TreeName = "InFlight" };
            var activeRec = MakeActiveSplittableRecording(tree);
            RecordingStore.AddRecordingWithTreeForTesting(activeRec);
            RecordingStore.CommittedTrees.Add(tree);

            RecordingStore.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            var tip = InFlightCommitOptimization.ResolveChainTip(
                activeRec, RecordingStore.CommittedRecordings);
            Assert.NotSame(activeRec, tip);
            // The hazard the carry exists for: the split gave the vessel line a new tip that
            // holds the snapshot and terminal but no spawn stamp.
            Assert.NotNull(tip.VesselSnapshot);
            Assert.Null(activeRec.VesselSnapshot);
            Assert.False(tip.VesselSpawned);
            Assert.True(activeRec.VesselSpawned);

            Assert.True(InFlightCommitOptimization.CarrySpawnStampToTip(activeRec, tip));
            Assert.True(tip.VesselSpawned);
            Assert.Equal(42u, tip.SpawnedVesselPersistentId);
            Assert.False(activeRec.VesselSpawned);
            Assert.Equal(0u, activeRec.SpawnedVesselPersistentId);
        }

        // --- Source gate: the pass runs between the commit and the leaf spawn ---

        private static readonly string CrLf = new string(new[] { (char)13, (char)10 });
        private static readonly string Lf = new string((char)10, 1);

        private static string LocateParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = System.IO.Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }

        [Fact]
        public void CommitTreeFlight_RunsOptimizationBetweenCommitAndLeafSpawn()
        {
            string path = LocateParsekFlightSource();
            Assert.True(System.IO.File.Exists(path), $"ParsekFlight.cs not found at {path}");
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                System.IO.File.ReadAllText(path).Replace(CrLf, Lf));

            const string decl = "public void CommitTreeFlight()";
            int declIdx = prepared.IndexOf(decl, StringComparison.Ordinal);
            Assert.True(declIdx >= 0, "CommitTreeFlight declaration not found");
            Assert.Equal(declIdx, prepared.LastIndexOf(decl, StringComparison.Ordinal));
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', declIdx));

            int commitIdx = body.IndexOf("RecordingStore.CommitTree(activeTree);", StringComparison.Ordinal);
            int passIdx = body.IndexOf("RunInFlightCommitOptimizationPass(activeRec, activeRecId)", StringComparison.Ordinal);
            int ledgerIdx = body.IndexOf("LedgerOrchestrator.NotifyLedgerTreeCommitted(activeTree);", StringComparison.Ordinal);
            int spawnIdx = body.IndexOf("SpawnTreeLeaves(activeTree, activeTipId);", StringComparison.Ordinal);
            Assert.True(commitIdx >= 0, "CommitTreeFlight no longer commits through RecordingStore.CommitTree");
            Assert.True(passIdx > commitIdx, "CommitTreeFlight does not run the optimization pass after the commit");
            Assert.True(ledgerIdx > passIdx, "the optimization pass must run before the ledger notify (MergeCommit order)");
            Assert.True(spawnIdx > passIdx, "the leaves must spawn after the pass, with the resolved active tip id");

            const string helperDecl = "private string RunInFlightCommitOptimizationPass(Recording activeRec, string activeRecId)";
            int helperIdx = prepared.IndexOf(helperDecl, StringComparison.Ordinal);
            Assert.True(helperIdx >= 0, "RunInFlightCommitOptimizationPass declaration not found");
            string helper = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', helperIdx));
            Assert.Contains("InFlightCommitOptimization.DecideSkipReason(", helper);
            Assert.Contains("InFlightCommitOptimization.CarrySpawnStampToTip(", helper);
            // The pass runs only through the guarded runner, never as a bare call that could
            // throw out of the commit before the ledger notify and the leaf spawn.
            Assert.Contains("InFlightCommitOptimization.RunPassAndResolveActiveTip(", helper);
            Assert.Contains("RecordingStore.RunOptimizationPass,", helper);
            Assert.DoesNotContain("RunOptimizationPass()", helper);
            Assert.DoesNotContain("RunOptimizationPass()", body);
        }
    }
}
