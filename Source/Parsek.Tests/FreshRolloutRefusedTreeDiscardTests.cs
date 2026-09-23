using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME, operator ruling 2026-09-23: when a
    /// restore coroutine refuses to put the pending tree on the scene-entry fresh
    /// rollout, a pending tree that is a RESUMED copy of a committed tree AND recorded
    /// nothing meaningful after the load (a few idle seconds) is discarded through the
    /// merge dialog's Discard steps. Committed history stays exactly as committed
    /// (tree, recordings, sidecars, pre-cutoff events). A copy that recorded meaningful
    /// content, a genuinely new tree, a Re-Fly-owned tree, a tree under an active merge
    /// journal and a replaced slot keep today's behaviour.
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
        private const string SegmentId = "rec_switch_segment";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> cleanupRoots = new List<string>();
        private readonly List<string> savedNames = new List<string>();
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
            RecordingStore.SaveGameForTesting = (name, folder, mode) => { savedNames.Add(name); return "ok"; };
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
            RecordingStore.SaveGameForTesting = null;
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
        /// The idle resumed copy: a deep clone of the committed tree whose tip sat still for
        /// about three seconds after the load (points 201..203, a stationary section).
        /// </summary>
        private static RecordingTree MakeIdleResumedCopy(RecordingTree committed)
        {
            var clone = RecordingTree.DeepClone(committed);
            var tip = clone.Recordings[TipId];
            tip.Points.Add(new TrajectoryPoint { ut = 201.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 203.0 });
            tip.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = 201.0, endUT = 203.0,
            });
            tip.FilesDirty = true;
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

        private static ParsekScenario InstallScenario()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static ParsekFlight.FreshRolloutRefusedTreeDisposition DisposeRefused(string site = "quickload-restore")
            => ParsekFlight.DisposeFreshRolloutRefusedPendingTree(site, RecordingStore.PendingTree, 760196917u);

        // ---------------- pure classifier ----------------

        [Fact]
        public void Classify_CoversEveryOutcome()
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "Kerbal X" };

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.NoPendingTree,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(null, true, true, false, false, true));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, true, false, false, true));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepSlotReplaced,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, false, true, false, false, true));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepReFlyOwned,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, true, true, false, true));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepMergeJournalActive,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, true, false, true, true));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, false, false, false, true));
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepMeaningfulPostLoadContent,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(tree, true, true, false, false, false));
            var noId = new RecordingTree { Id = null, TreeName = "x" };
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy,
                ParsekFlight.ClassifyFreshRolloutRefusedTree(noId, true, true, false, false, true));
        }

        [Fact]
        public void HasCommittedTreeWithId_MatchesOnlyCommittedIds()
        {
            CommitStationTree();
            Assert.True(RecordingStore.HasCommittedTreeWithId(TreeId));
            Assert.False(RecordingStore.HasCommittedTreeWithId("tree_other"));
            Assert.False(RecordingStore.HasCommittedTreeWithId(null));
        }

        // ---------------- post-load content predicate ----------------

        [Fact]
        public void Tail_IdleSeconds_IsNoOp_WithSpanAndPoints()
        {
            var committed = CommitStationTree();
            var r = RefusedResumedCopyTail.Evaluate(MakeIdleResumedCopy(committed), committed);
            Assert.True(r.IsNoOp, r.KeepReason);
            Assert.Equal(2, r.TailPointCount);
            Assert.Equal(201.0, r.TailFromUT);
            Assert.Equal(203.0, r.TailToUT);
        }

        [Fact]
        public void Tail_UnchangedClone_IsNoOp_EvenWithDestroyedCommittedDebris()
        {
            // Committed history is never a "tail": a debris recording destroyed in the
            // original flight must not make an unchanged clone look meaningful.
            var committed = CommitStationTree();
            var debris = MakeRec("rec_debris", TreeId, 42u, 120.0, 150.0);
            debris.TerminalStateValue = TerminalState.Destroyed;
            debris.VesselDestroyed = true;
            committed.AddOrReplaceRecording(debris);
            var r = RefusedResumedCopyTail.Evaluate(RecordingTree.DeepClone(committed), committed);
            Assert.True(r.IsNoOp, r.KeepReason);
            Assert.Equal(0, r.TailPointCount);
            Assert.True(double.IsNaN(r.TailFromUT));
        }

        [Fact]
        public void Tail_MeaningfulPartEvent_Keeps()
        {
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.Recordings[TipId].PartEvents.Add(new PartEvent
            {
                ut = 202.0, partPersistentId = 7u, eventType = PartEventType.Decoupled,
            });
            var r = RefusedResumedCopyTail.Evaluate(copy, committed);
            Assert.False(r.IsNoOp);
            Assert.StartsWith("meaningful-tail:" + TipId + ":part-event:Decoupled", r.KeepReason);
        }

        [Fact]
        public void Tail_NonBoringSection_Keeps()
        {
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.Recordings[TipId].TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric, startUT = 203.0, endUT = 210.0,
            });
            var r = RefusedResumedCopyTail.Evaluate(copy, committed);
            Assert.False(r.IsNoOp);
            Assert.Contains("non-boring-section:Atmospheric", r.KeepReason);
        }

        [Fact]
        public void Tail_LongIdleContinuation_Keeps()
        {
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.Recordings[TipId].Points.Add(new TrajectoryPoint { ut = 400.0 });
            var r = RefusedResumedCopyTail.Evaluate(copy, committed);
            Assert.False(r.IsNoOp);
            Assert.StartsWith("long-tail:" + TipId, r.KeepReason);
        }

        [Fact]
        public void Tail_FlownSwitchSegmentRecording_Keeps()
        {
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.AddOrReplaceRecording(MakeRec(SegmentId, TreeId, 555u, 201.0, 203.0));
            var r = RefusedResumedCopyTail.Evaluate(copy, committed);
            Assert.False(r.IsNoOp);
            Assert.Equal("pending-only-recording:" + SegmentId, r.KeepReason);
        }

        [Fact]
        public void Tail_NewBranchPoint_Keeps()
        {
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.BranchPoints.Add(new BranchPoint { Id = "bp_new", UT = 202.0, Type = BranchPointType.Undock });
            var r = RefusedResumedCopyTail.Evaluate(copy, committed);
            Assert.False(r.IsNoOp);
            Assert.StartsWith("new-branch-point:bp_new", r.KeepReason);
        }

        // ---------------- the discard ----------------

        [Fact]
        public void IdleResumedCopy_QuickloadRefusal_IsDiscarded_CommittedHistoryUntouched()
        {
            StageSaveRoot();
            var committed = CommitStationTree();
            StageSidecars(TipId);
            StageSidecars(BgId);
            AddTechEvent(TipId, 150.0, "committedTech");
            AddTechEvent(TipId, 202.0, "resumedTech");
            var committedTip = committed.Recordings[TipId];
            int committedTipPoints = committedTip.Points.Count;

            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);
            logLines.Clear();

            var d = DisposeRefused();

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
            // Committed sidecars survive.
            Assert.Equal(Suffixes.Length, CountSidecars(TipId));
            Assert.Equal(Suffixes.Length, CountSidecars(BgId));
            // Committed-era event kept; the resumed seconds' event purged.
            Assert.Contains(GameStateStore.Events, e => e.key == "committedTech");
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "resumedTech");
            // The restore attempt is released.
            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            // Not serialized: no save refresh.
            Assert.Empty(savedNames);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("Fresh-rollout refusal (quickload-restore): discarding resumed copy")
                && l.Contains("id=" + TreeId)
                && l.Contains("droppedSpan=201.0..203.0 (2.0s)") && l.Contains("droppedPoints=2")
                && l.Contains("restoreAttemptArmed=True") && l.Contains("refusedPid=760196917"));
        }

        [Fact]
        public void MeaningfulResumedCopy_IsKeptPending_AndLogsWhy()
        {
            StageSaveRoot();
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.AddOrReplaceRecording(MakeRec(SegmentId, TreeId, 555u, 201.0, 203.0));
            StageSidecars(SegmentId);
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);
            logLines.Clear();

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepMeaningfulPostLoadContent, d);
            Assert.Same(copy, RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Equal(Suffixes.Length, CountSidecars(SegmentId));
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("keeping resumed copy of committed tree")
                && l.Contains("reason=pending-only-recording:" + SegmentId)
                && l.Contains("left pending"));
            Assert.DoesNotContain(logLines, l => l.Contains("discarding resumed copy"));
        }

        [Fact]
        public void IdleOutsiderChainVesselSwitchCopy_IsDiscarded()
        {
            // Gap 1 of the todo: a LimboVesselSwitch stash with no active recording has no
            // revertible pre-transition. As an idle resumed committed copy it is now
            // discarded instead of lingering as a LimboVesselSwitch a later load would reinstall.
            var committed = CommitStationTree();
            var copy = RecordingTree.DeepClone(committed);
            copy.ActiveRecordingId = null;
            RecordingStore.StashPendingTree(copy, PendingTreeState.LimboVesselSwitch);
            Assert.False(ParsekFlight.TryRevertPreTransitionForVesselSwitch(copy, out _));

            var d = DisposeRefused("vessel-switch-restore");

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy, d);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Same(committed, RecordingStore.CommittedTrees[0]);
            Assert.Contains(logLines, l =>
                l.Contains("Fresh-rollout refusal (vessel-switch-restore): discarding resumed copy")
                && l.Contains("state=LimboVesselSwitch") && l.Contains("droppedSpan=none"));
        }

        [Fact]
        public void Discard_ClearsASwitchSegmentSessionBoundToTheTree()
        {
            var committed = CommitStationTree();
            var scenario = InstallScenario();
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = TreeId,
                ActiveSegmentRecordingId = TipId,
                SwitchUT = 201.0,
                PreSessionBranchPointIds = new List<string>(),
            });
            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy, d);
            Assert.Null(scenario.ActiveSwitchSegmentSession);
            Assert.Contains(logLines, l => l.Contains("[SwitchSegment]") && l.Contains("cleared:")
                && l.Contains("fresh-rollout refusal discarded resumed committed copy tree=" + TreeId));
        }

        [Fact]
        public void Discard_LeavesASessionForAnotherTreeAlone()
        {
            var committed = CommitStationTree();
            var scenario = InstallScenario();
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = "tree_other",
                PreSessionBranchPointIds = new List<string>(),
            });
            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);

            DisposeRefused();

            Assert.NotNull(scenario.ActiveSwitchSegmentSession);
        }

        [Fact]
        public void Discard_OfASerializedCopy_RefreshesPersistentOnly()
        {
            var committed = CommitStationTree();
            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);
            RecordingStore.MarkPendingTreeSerializedForSave("test");

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy, d);
            Assert.Equal(new[] { "persistent" }, savedNames.ToArray());
            Assert.Contains(logLines, l => l.Contains("serialized=True"));
        }

        [Fact]
        public void ActiveMergeJournal_KeepsTheCopy()
        {
            var committed = CommitStationTree();
            var scenario = InstallScenario();
            scenario.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "journal_refused", SessionId = "sess", Phase = MergeJournal.Phases.Supersede,
            };
            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepMergeJournalActive, d);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l => l.Contains("merge journal journal_refused is active"));
        }

        [Fact]
        public void ReplacedSlot_IsNeverDiscarded()
        {
            var committed = CommitStationTree();
            var refused = MakeIdleResumedCopy(committed);
            RecordingStore.StashPendingTree(refused, PendingTreeState.Limbo);
            var replacement = MakeIdleResumedCopy(committed);
            RecordingStore.StashPendingTree(replacement, PendingTreeState.Limbo);

            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", refused, 7u);

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepSlotReplaced, d);
            Assert.Same(replacement, RecordingStore.PendingTree);
            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]") && l.Contains("not the refused tree"));
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

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy, d);
            Assert.Same(fresh, RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Equal(Suffixes.Length, CountSidecars("rec_new"));
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
            var copy = MakeIdleResumedCopy(committed);
            Assert.True(RecordingStore.RemoveCommittedTreeById(TreeId, "test detach"));
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepNotACommittedCopy, d);
            Assert.Same(copy, RecordingStore.PendingTree);
        }

        [Fact]
        public void ReFlyOwnedResumedCopy_IsKept()
        {
            var committed = CommitStationTree();
            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);
            RewindInvokeContext.Pending = true;

            var d = DisposeRefused();

            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.KeepReFlyOwned, d);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l => l.Contains("a Re-Fly session owns it"));
        }

        [Fact]
        public void NoPendingTree_IsANoOp()
        {
            var d = ParsekFlight.DisposeFreshRolloutRefusedPendingTree("quickload-restore", null, 7u);
            Assert.Equal(ParsekFlight.FreshRolloutRefusedTreeDisposition.NoPendingTree, d);
        }

        // ---------------- lifetime after the refusal ----------------

        [Fact]
        public void SaveLoad_AfterDiscard_LeavesNoPendingTree_AndKeepsTheCommittedNode()
        {
            var committed = CommitStationTree();
            var copy = MakeIdleResumedCopy(committed);
            copy.Recordings[TipId].FilesDirty = false;
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);
            DisposeRefused();

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
            var copy = MakeIdleResumedCopy(committed);
            copy.Recordings[TipId].FilesDirty = false;
            RecordingStore.StashPendingTree(copy, PendingTreeState.Limbo);

            var node = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Contains(node.GetNodes("RECORDING_TREE"), n => ParsekScenario.IsActiveTreeNode(n));
        }

        [Fact]
        public void LaterStash_AfterDiscard_DoesNotWarnAboutOverwriting()
        {
            var committed = CommitStationTree();
            RecordingStore.StashPendingTree(MakeIdleResumedCopy(committed), PendingTreeState.Limbo);
            DisposeRefused();
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
            // The residual filed as FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING: a kept refused
            // tree keeps today's behaviour, including the overwrite Warn.
            var fresh = new RecordingTree
            {
                Id = "tree_new_uncommitted", TreeName = "First Launch", RootRecordingId = "rec_new",
                ActiveRecordingId = "rec_new",
            };
            fresh.AddOrReplaceRecording(MakeRec("rec_new", "tree_new_uncommitted", 99u, 300.0, 310.0));
            RecordingStore.StashPendingTree(fresh, PendingTreeState.Limbo);
            DisposeRefused();
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
