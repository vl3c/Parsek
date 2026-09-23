using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME, operator ruling 2026-09-23: when a
    /// restore coroutine refuses to put the pending tree on the scene-entry fresh
    /// rollout, a pending tree that is a RESUMED copy of a committed tree is discarded
    /// through the merge dialog's Discard primitive. Committed history stays exactly as
    /// committed (tree, recordings, sidecars, pre-cutoff events), only the resumed
    /// seconds go, and nothing is left pending for a later stash to overwrite. A
    /// genuinely new tree and a Re-Fly-owned tree keep today's behaviour.
    ///
    /// <para>The two refusal sites are Unity coroutines with no headless seam; both call
    /// <see cref="ParsekFlight.DisposeFreshRolloutRefusedPendingTree"/>, which the file
    /// cells drive with a real committed store, real staged sidecars and the real
    /// discard. The source gates pin that each coroutine reaches it before the pop.</para>
    /// </summary>
    [Collection("Sequential")]
    public class FreshRolloutRefusedTreeDiscardTests : IDisposable
    {
        private const string TreeId = "tree_station_8c677bba";
        private const string TipId = "rec_station_tip";
        private const string BgId = "rec_station_probe";
        private const string ResumedOnlyId = "rec_resumed_debris";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> cleanupRoots = new List<string>();
        private string recordingsDir;

        public FreshRolloutRefusedTreeDiscardTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindInvokeContext.Clear();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 300.0;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try
                {
                    if (Directory.Exists(cleanupRoots[i]))
                        Directory.Delete(cleanupRoots[i], true);
                }
                catch { }
            }
            ParsekScenario.CurrentTimelineUTProviderForTesting = null;
            ParsekScenario.SetInstanceForTesting(null);
            RewindInvokeContext.Clear();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------------- fixture ----------------

        private static readonly string[] Suffixes = { ".prec", "_vessel.craft", "_ghost.craft" };

        private void StageSaveRoot()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "parsek-refused-tree-" + Guid.NewGuid().ToString("N"));
            recordingsDir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            cleanupRoots.Add(root);
            RecordingPaths.SaveRootOverrideForTesting = root;
        }

        private void StageSidecars(string recordingId)
        {
            foreach (string s in Suffixes)
                File.WriteAllText(Path.Combine(recordingsDir, recordingId + s), "committed-bytes");
        }

        private int CountSidecars(string recordingId)
        {
            int n = 0;
            foreach (string s in Suffixes)
                if (File.Exists(Path.Combine(recordingsDir, recordingId + s)))
                    n++;
            return n;
        }

        private static Recording MakeRec(string id, string treeId, uint pid, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "Kerbal X",
                VesselPersistentId = pid,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            rec.Points.Add(new TrajectoryPoint { ut = endUT });
            return rec;
        }

        /// <summary>
        /// Commits the station tree (tip + background probe, 100..200) and arms the
        /// committed-tree restore attempt the way TryTakeCommittedTreeForSpawnedVesselRestore
        /// does. Returns the committed original.
        /// </summary>
        private static RecordingTree CommitStationTree()
        {
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Kerbal X",
                RootRecordingId = TipId, ActiveRecordingId = TipId,
            };
            var tip = MakeRec(TipId, TreeId, 3620499050u, 100.0, 200.0);
            var bg = MakeRec(BgId, TreeId, 1223410921u, 100.0, 200.0);
            tree.AddOrReplaceRecording(tip);
            tree.AddOrReplaceRecording(bg);
            tree.BackgroundMap[1223410921u] = BgId;
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(tip);
            RecordingStore.AddRecordingWithTreeForTesting(bg);
            RecordingStore.ArmCommittedTreeRestoreAttempt(
                tree, "TryTakeCommittedTreeForSpawnedVesselRestore copy-on-write");
            return tree;
        }

        /// <summary>
        /// The resumed copy: a deep clone of the committed tree whose tip recorded a few
        /// seconds after the load, plus a pending-only recording born in those seconds.
        /// </summary>
        private static RecordingTree MakeResumedCopy(RecordingTree committed)
        {
            var clone = RecordingTree.DeepClone(committed);
            clone.Recordings[TipId].Points.Add(new TrajectoryPoint { ut = 203.0 });
            clone.Recordings[TipId].FilesDirty = true;
            clone.AddOrReplaceRecording(MakeRec(ResumedOnlyId, TreeId, 555u, 201.0, 203.0));
            return clone;
        }

        private static void AddTechEvent(string recordingId, double ut, string key)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.TechResearched,
                key = key,
                recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref e);
        }

        // ---------------- pure classifier ----------------

        [Fact]
        public void Classify_CoversEveryOutcome()
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "Kerbal X" };

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.NoPendingTree,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(null, true, false));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, false));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, false, false));
            // Re-Fly ownership wins even over a committed id.
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepReFlyOwned,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, true));
            var noId = new RecordingTree { Id = null, TreeName = "x" };
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(noId, true, false));
        }

        [Fact]
        public void HasCommittedTreeWithId_MatchesOnlyCommittedIds()
        {
            CommitStationTree();
            Assert.True(RecordingStore.HasCommittedTreeWithId(TreeId));
            Assert.False(RecordingStore.HasCommittedTreeWithId("tree_other"));
            Assert.False(RecordingStore.HasCommittedTreeWithId(null));
        }

        // ---------------- the discard ----------------

        [Fact]
        public void ResumedCopy_QuickloadRefusal_IsDiscarded_CommittedHistoryUntouched()
        {
            StageSaveRoot();
            var committed = CommitStationTree();
            StageSidecars(TipId);
            StageSidecars(BgId);
            StageSidecars(ResumedOnlyId);
            AddTechEvent(TipId, 150.0, "committedTech");
            AddTechEvent(TipId, 202.0, "resumedTech");
            AddTechEvent(ResumedOnlyId, 202.5, "resumedOnlyTech");
            var committedTip = committed.Recordings[TipId];
            int committedTipPoints = committedTip.Points.Count;

            RecordingStore.StashPendingTree(MakeResumedCopy(committed), PendingTreeState.Limbo);
            logLines.Clear();

            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 760196917u);

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy, d);
            Assert.False(RecordingStore.HasPendingTree);
            // Committed tree and its recordings: same objects, same data.
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Same(committed, RecordingStore.CommittedTrees[0]);
            Assert.Equal(2, committed.Recordings.Count);
            Assert.Same(committedTip, RecordingStore.CommittedTrees[0].Recordings[TipId]);
            Assert.Equal(committedTipPoints, committedTip.Points.Count);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == TipId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == BgId);
            // Committed sidecars survive; the resumed-only recording's files are gone.
            Assert.Equal(Suffixes.Length, CountSidecars(TipId));
            Assert.Equal(Suffixes.Length, CountSidecars(BgId));
            Assert.Equal(0, CountSidecars(ResumedOnlyId));
            // Committed-era event kept; the resumed seconds' events purged.
            Assert.Contains(GameStateStore.Events, e => e.key == "committedTech");
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "resumedTech");
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "resumedOnlyTech");
            // The restore attempt is released.
            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Fresh-rollout refusal (quickload-restore): discarding resumed copy")
                && l.Contains("id=" + TreeId)
                && l.Contains("committedOverlap=2") && l.Contains("pendingOnly=1")
                && l.Contains("restoreAttemptArmed=True") && l.Contains("refusedPid=760196917"));
        }

        [Fact]
        public void ResumedCopy_OutsiderChainVesselSwitchStash_IsDiscarded()
        {
            // Gap 1 of the todo: a LimboVesselSwitch stash with no active recording has no
            // revertible pre-transition. As a resumed committed copy it is now discarded
            // instead of lingering as a LimboVesselSwitch a later load would reinstall.
            var committed = CommitStationTree();
            var copy = RecordingTree.DeepClone(committed);
            copy.ActiveRecordingId = null;
            RecordingStore.StashPendingTree(copy, PendingTreeState.LimboVesselSwitch);
            Assert.False(ParsekFlight.TryRevertPreTransitionForVesselSwitch(copy, out _));

            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("vessel-switch-restore", 42u);

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy, d);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Same(committed, RecordingStore.CommittedTrees[0]);
            Assert.Contains(logLines, l =>
                l.Contains("Fresh-rollout refusal (vessel-switch-restore): discarding resumed copy")
                && l.Contains("state=LimboVesselSwitch"));
        }

        [Fact]
        public void GenuinelyNewTree_IsKept_WithItsFilesAndState()
        {
            StageSaveRoot();
            CommitStationTree();
            var fresh = new RecordingTree
            {
                Id = "tree_new_uncommitted", TreeName = "Interceptor",
                RootRecordingId = "rec_new", ActiveRecordingId = "rec_new",
            };
            fresh.AddOrReplaceRecording(MakeRec("rec_new", "tree_new_uncommitted", 99u, 300.0, 310.0));
            StageSidecars("rec_new");
            RecordingStore.StashPendingTree(fresh, PendingTreeState.Limbo);

            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy, d);
            Assert.Same(fresh, RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Equal(Suffixes.Length, CountSidecars("rec_new"));
            // The unrelated committed tree's restore attempt is not released.
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("keeping pending tree 'Interceptor'")
                && l.Contains("a genuinely new tree") && l.Contains("not a discardable resumed copy; kept"));
            Assert.DoesNotContain(logLines, l => l.Contains("discarding resumed copy"));
        }

        [Fact]
        public void SameIdTree_WhoseCommittedCopyWasDetached_IsKept()
        {
            // Mirror direction: a save-loaded isActive node detaches the committed copy
            // (TryRestoreActiveTreeNode -> RemoveCommittedTreeById), leaving the pending
            // tree as the ONLY holder of that history. It must not be discarded.
            var committed = CommitStationTree();
            var copy = MakeResumedCopy(committed);
            Assert.True(RecordingStore.RemoveCommittedTreeById(TreeId, "test detach"));
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);

            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy, d);
            Assert.Same(copy, RecordingStore.PendingTree);
        }

        [Fact]
        public void ReFlyOwnedResumedCopy_IsKept()
        {
            var committed = CommitStationTree();
            RecordingStore.StashPendingTree(MakeResumedCopy(committed), PendingTreeState.Limbo);
            RewindInvokeContext.Pending = true;

            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepReFlyOwned, d);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l => l.Contains("a Re-Fly session owns it"));
        }

        [Fact]
        public void NoPendingTree_IsANoOp()
        {
            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.NoPendingTree, d);
        }

        // ---------------- lifetime after the refusal ----------------

        [Fact]
        public void SaveLoad_AfterDiscard_LeavesNoPendingTree_AndKeepsTheCommittedNode()
        {
            var committed = CommitStationTree();
            var copy = MakeResumedCopy(committed);
            copy.Recordings[TipId].FilesDirty = false;
            copy.Recordings[ResumedOnlyId].FilesDirty = false;
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);
            ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode[] trees = node.GetNodes("RECORDING_TREE");
            Assert.Single(trees);
            Assert.Equal(TreeId, trees[0].GetValue("id"));
            Assert.False(ParsekScenario.IsActiveTreeNode(trees[0]));
            Assert.False(ParsekScenario.IsPendingTreeNode(trees[0]));

            RecordingStore.ResetForTesting();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(node));
            Assert.False(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void SaveLoad_WithoutDiscard_ParksTheCopyAsAnActiveResumeNode()
        {
            // Control for the cell above: the lingering copy the ruling removes is written
            // as an isActive node and reloads into the pending slot.
            var committed = CommitStationTree();
            var copy = MakeResumedCopy(committed);
            copy.Recordings[TipId].FilesDirty = false;
            copy.Recordings[ResumedOnlyId].FilesDirty = false;
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Contains(node.GetNodes("RECORDING_TREE"), n => ParsekScenario.IsActiveTreeNode(n));
        }

        [Fact]
        public void LaterStash_AfterDiscard_DoesNotWarnAboutOverwriting()
        {
            var committed = CommitStationTree();
            RecordingStore.StashPendingTree(MakeResumedCopy(committed), PendingTreeState.Limbo);
            ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);
            logLines.Clear();

            var launch = new RecordingTree
            {
                Id = "tree_launch", TreeName = "Interceptor", RootRecordingId = "rec_launch",
            };
            launch.AddOrReplaceRecording(MakeRec("rec_launch", "tree_launch", 99u, 300.0, 400.0));
            RecordingStore.StashPendingTree(launch, PendingTreeState.Finalized);

            Assert.Same(launch, RecordingStore.PendingTree);
            Assert.DoesNotContain(logLines, l => l.Contains("overwriting existing pending tree"));
        }

        [Fact]
        public void LaterStash_OverAKeptNewTree_StillWarns()
        {
            // The residual filed as FRESH-LAUNCH-REFUSED-NEW-TREE-PENDING: a genuinely new
            // refused tree keeps today's behaviour, including the overwrite Warn.
            var fresh = new RecordingTree
            {
                Id = "tree_new_uncommitted", TreeName = "First Launch", RootRecordingId = "rec_new",
                ActiveRecordingId = "rec_new",
            };
            fresh.AddOrReplaceRecording(MakeRec("rec_new", "tree_new_uncommitted", 99u, 300.0, 310.0));
            RecordingStore.StashPendingTree(fresh, PendingTreeState.Limbo);
            ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", 7u);
            logLines.Clear();

            var launch = new RecordingTree { Id = "tree_launch", TreeName = "Second Launch" };
            RecordingStore.StashPendingTree(launch, PendingTreeState.Finalized);

            Assert.Contains(logLines, l => l.Contains("overwriting existing pending tree 'First Launch'"));
        }

        // ---------------- source gates over the two coroutines ----------------

        private static string ReadSource(string file)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", file);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static string MethodBody(string source, string declaration)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(source);
            int decl = prepared.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(decl >= 0, "declaration not found: " + declaration);
            Assert.Equal(decl, prepared.LastIndexOf(declaration, StringComparison.Ordinal));
            int open = prepared.IndexOf('{', decl);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string needle, int from = 0)
        {
            int i = body.IndexOf(needle, from, StringComparison.Ordinal);
            Assert.True(i >= 0, "needle not found in body: " + needle);
            return i;
        }

        [Fact]
        public void SourceGate_VesselSwitchRefusal_DisposesBeforeRevertAndPop()
        {
            string body = MethodBody(ReadSource("ParsekFlight.cs"),
                "IEnumerator RestoreActiveTreeFromPendingForVesselSwitch()");
            int refuse = IndexOrFail(body, "ShouldRefuseVesselSwitchRestoreForFreshRollout(");
            int dispose = IndexOrFail(body, "DisposeFreshRolloutRefusedPendingTree(", refuse);
            int notDiscard = IndexOrFail(body,
                "!= FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy", dispose);
            int revert = IndexOrFail(body, "TryRevertPreTransitionForVesselSwitch(", notDiscard);
            int yieldBreak = IndexOrFail(body, "yield break;", revert);
            int pop = IndexOrFail(body, "RecordingStore.PopPendingTree()");
            Assert.True(yieldBreak < pop,
                "the refusal branch (dispose, then the revert only when not discarded) must " +
                "yield break before the tree is popped");
        }

        [Fact]
        public void SourceGate_QuickloadRefusal_DisposesBeforeTheLimboGiveUpAndPop()
        {
            string body = MethodBody(ReadSource("ParsekFlight.cs"),
                "IEnumerator RestoreActiveTreeFromPending()");
            int fresh = IndexOrFail(body, "QuickloadResumeMatchGuard.IsFreshRolloutCandidate(");
            int record = IndexOrFail(body, "refusedFreshRolloutPid = v.persistentId;", fresh);
            IndexOrFail(body, "break;", record);
            int dispose = IndexOrFail(body, "DisposeFreshRolloutRefusedPendingTree(", record);
            int discardEq = IndexOrFail(body,
                "== FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy", dispose);
            int yieldBreak = IndexOrFail(body, "yield break;", discardEq);
            int giveUp = IndexOrFail(body, "EvaluateRestoreGiveUp(", dispose);
            int pop = IndexOrFail(body, "RecordingStore.PopPendingTree()");
            Assert.True(yieldBreak < giveUp && giveUp < pop,
                "the discard must run after the match loop and before the Limbo give-up and the pop");
        }
    }
}
