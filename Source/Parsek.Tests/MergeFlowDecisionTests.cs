using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A fake <see cref="IMergeFlowHost"/>: the recorder session is plain state, every host
    /// operation is recorded by name (in call order) and does nothing else, except the two
    /// stash operations, which stash the tree the way the host does so a test can read the
    /// pending slot.
    /// </summary>
    internal sealed class FakeMergeFlowHost : IMergeFlowHost
    {
        internal readonly List<string> Calls = new List<string>();
        internal bool CanPreTransition = true;
        internal bool VesselSwitchPendingFresh;
        internal double UniversalTime = 500.0;
        internal uint ActiveVesselPid;
        internal RecordingFinalizationCache CacheToResolve;
        internal readonly List<string> ScreenMessages = new List<string>();
        internal string SpawnedLeavesActiveId;
        internal RecordingTree SpawnedLeavesTree;

        public RecordingTree activeTree { get; set; }
        public FlightRecorder recorder { get; set; }
        public BackgroundRecorder backgroundRecorder { get; set; }
        public int lastPlaybackIndex { get; set; }

        public RecorderStateSnapshot CaptureRecorderState()
        {
            Calls.Add("CaptureRecorderState");
            return default(RecorderStateSnapshot);
        }

        public void FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree)
        {
            Calls.Add("FlushRecorderToTreeRecording:" + (tree?.Id ?? "<null>"));
        }

        public void FlushRecorderIntoActiveTreeForSerialization()
        {
            Calls.Add("FlushRecorderIntoActiveTreeForSerialization");
        }

        public RecordingFinalizationCache ResolveFinalizationCacheForRecording(
            RecordingTree tree, Recording recording)
        {
            Calls.Add("ResolveFinalizationCacheForRecording:" + (recording?.RecordingId ?? "<null>"));
            return CacheToResolve;
        }

        public bool CanPreTransitionForVesselSwitch()
        {
            Calls.Add("CanPreTransitionForVesselSwitch");
            return CanPreTransition;
        }

        public void StashActiveTreeForVesselSwitch(double commitUT)
        {
            Calls.Add("StashActiveTreeForVesselSwitch:" + commitUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            PendingTreeSlot.StashPendingTree(activeTree, PendingTreeState.LimboVesselSwitch);
        }

        public void StashActiveTreeAsPendingLimbo(double commitUT)
        {
            Calls.Add("StashActiveTreeAsPendingLimbo:" + commitUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            PendingTreeSlot.StashPendingTree(activeTree, PendingTreeState.Limbo);
        }

        public void ClearSceneChangeTransientState()
        {
            Calls.Add("ClearSceneChangeTransientState");
        }

        public void ScreenMessage(string message, float duration)
        {
            Calls.Add("ScreenMessage");
            ScreenMessages.Add(message);
        }

        public void Log(string message)
        {
            Calls.Add("Log:" + message);
        }

        public void SpawnTreeLeaves(RecordingTree tree, string activeRecordingId)
        {
            Calls.Add("SpawnTreeLeaves");
            SpawnedLeavesTree = tree;
            SpawnedLeavesActiveId = activeRecordingId;
        }

        public void EvaluateAndApplyGhostChains()
        {
            Calls.Add("EvaluateAndApplyGhostChains");
        }

        public double CurrentUniversalTime()
        {
            Calls.Add("CurrentUniversalTime");
            return UniversalTime;
        }

        public bool IsVesselSwitchPendingFresh()
        {
            Calls.Add("IsVesselSwitchPendingFresh");
            return VesselSwitchPendingFresh;
        }

        public uint ActiveVesselPersistentIdOrZero()
        {
            Calls.Add("ActiveVesselPersistentIdOrZero");
            return ActiveVesselPid;
        }

        internal bool Called(string prefix) => Calls.Exists(c => c.StartsWith(prefix, StringComparison.Ordinal));

        internal int IndexOf(string prefix) => Calls.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The merge flow's decisions driven through <see cref="FakeMergeFlowHost"/>, with no flight
    /// scene: which scene-exit path runs (restore guard, suppressed discard, vessel-switch
    /// pre-transition, Limbo stash, save-file load and quit-without-saving stash, auto-merge off
    /// stash, auto-merge commit), the post-finalize teardown, the in-flight commit, the
    /// auto-discard teardown, idle-on-pad's flush guard, the live recording lookup and the
    /// recorder stop/flush in FinalizeTreeRecordings.
    /// </summary>
    [Collection("Sequential")]
    public class MergeFlowDecisionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeFlowDecisionTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            CommittedTreeRestoreAttempt.ResetForTesting();
            Parsek.Logistics.RouteEventLog.ResetForTesting();
            SaveFileLoadIntent.ResetForTesting();
            NoSaveSceneExit.ResetForTesting();
            RevertDetector.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekFlight.restoringActiveTree = false;
            Patches.PhysicsFramePatch.ActiveRecorder = null;
        }

        // --- builders ---

        private static Recording Rec(string id, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Vessel " + id,
                TerminalStateValue = terminal,
                VesselSnapshot = new ConfigNode("VESSEL"),
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 20, bodyName = "Kerbin" });
            return rec;
        }

        private static RecordingTree Tree(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-d", TreeName = "Decision Tree" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            tree.RootRecordingId = recs[0].RecordingId;
            tree.ActiveRecordingId = recs[0].RecordingId;
            return tree;
        }

        private static (FakeMergeFlowHost host, ParsekFlightMergeFlow flow) Flow(RecordingTree tree)
        {
            var host = new FakeMergeFlowHost { activeTree = tree };
            return (host, new ParsekFlightMergeFlow(host));
        }

        private static void AutoMerge(bool on)
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { autoMerge = on };
        }

        private static FlightRecorder Recorder(bool isRecording, bool backgrounded, uint vesselPid = 0)
        {
            var rec = new FlightRecorder();
            rec.IsRecording = isRecording;
            typeof(FlightRecorder)
                .GetProperty("RecordingVesselId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(rec, vesselPid);
            if (isRecording && !backgrounded)
                Patches.PhysicsFramePatch.ActiveRecorder = rec;
            return rec;
        }

        private bool Logged(string tag, string text) =>
            logLines.Exists(l => l.Contains("[" + tag + "]") && l.Contains(text));

        // ================= scene-exit finalize: path selection =================

        [Fact]
        public void SceneExit_RestoreInProgress_SkipsAndKeepsTheTree()
        {
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);
            ParsekFlight.restoringActiveTree = true;

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.Same(tree, host.activeTree);
            Assert.Empty(host.Calls);
            Assert.False(PendingTreeSlot.HasPendingTree);
            Assert.True(Logged("Flight", "FinalizeTreeOnSceneChange: skipped")
                && Logged("Flight", "restore coroutine in progress (dest=SPACECENTER)"));
        }

        [Fact]
        public void SceneExit_SuppressionArmed_DiscardsWithoutStashAndConsumesTheSuppression()
        {
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);
            host.recorder = Recorder(isRecording: false, backgrounded: false);
            host.backgroundRecorder = new BackgroundRecorder(tree);
            PendingTreeSlot.ArmNextTreeSceneExitCommitSuppression("unit suppress");

            flow.FinalizeTreeOnSceneChange(GameScenes.SPACECENTER);

            Assert.Null(host.activeTree);
            Assert.Null(host.recorder);
            Assert.Null(host.backgroundRecorder);
            Assert.False(PendingTreeSlot.HasPendingTree);
            Assert.False(PendingTreeSlot.IsNextTreeSceneExitCommitSuppressionArmed);
            // The live entry logs the recorder state on the suppressed path, twice.
            Assert.Equal(2, host.Calls.FindAll(c => c == "CaptureRecorderState").Count);
            Assert.False(host.Called("StashActiveTree"));
            Assert.True(Logged("Flight", "suppressed tree scene-exit commit dest=SPACECENTER UT=500.0")
                && Logged("Flight", "reason='unit suppress' - discarding in-memory tree without STASH"));
        }

        [Fact]
        public void SceneExit_TestEntry_SuppressedPath_DoesNotLogRecorderState()
        {
            var (host, flow) = Flow(Tree(Rec("a")));
            PendingTreeSlot.ArmNextTreeSceneExitCommitSuppression("quiet");

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.False(host.Called("CaptureRecorderState"));
            Assert.Null(host.activeTree);
        }

        [Fact]
        public void SceneExit_FlightVesselSwitchSafe_PreTransitionsInsteadOfLimbo()
        {
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);
            host.VesselSwitchPendingFresh = true;
            host.CanPreTransition = true;

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.FLIGHT, 123);

            Assert.True(host.Called("StashActiveTreeForVesselSwitch:123"));
            Assert.False(host.Called("StashActiveTreeAsPendingLimbo"));
            Assert.Equal(PendingTreeState.LimboVesselSwitch, PendingTreeSlot.PendingTreeStateValue);
            Assert.Null(host.activeTree);
        }

        [Fact]
        public void SceneExit_FlightVesselSwitchUnsafe_FallsBackToLimboWithItsLine()
        {
            var (host, flow) = Flow(Tree(Rec("a")));
            host.VesselSwitchPendingFresh = true;
            host.CanPreTransition = false;

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.FLIGHT, 123);

            Assert.True(host.Called("StashActiveTreeAsPendingLimbo:123"));
            Assert.False(host.Called("StashActiveTreeForVesselSwitch"));
            Assert.True(Logged("Flight", "vessel switch detected but pre-transition is unsafe"));
            Assert.Equal(PendingTreeState.Limbo, PendingTreeSlot.PendingTreeStateValue);
        }

        [Fact]
        public void SceneExit_FlightNoVesselSwitch_LimboWithoutAskingPreTransition()
        {
            var (host, flow) = Flow(Tree(Rec("a")));

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.FLIGHT, 123);

            Assert.True(host.Called("StashActiveTreeAsPendingLimbo:123"));
            Assert.False(host.Called("CanPreTransitionForVesselSwitch"));
            Assert.False(Logged("Flight", "vessel switch detected"));
        }

        [Fact]
        public void SceneExit_SaveFileLoadOutOfFlight_StashesLimboInsteadOfCommitting()
        {
            AutoMerge(true);
            var tree = Tree(Rec("a", TerminalState.Orbiting));
            var (host, flow) = Flow(tree);
            SaveFileLoadIntent.Arm(50, "SPACECENTER", 0, 0);

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.True(host.Called("StashActiveTreeAsPendingLimbo:100"));
            Assert.Equal(PendingTreeState.Limbo, PendingTreeSlot.PendingTreeStateValue);
            Assert.True(Logged("Flight", "FinalizeTreeOnSceneChange: save-file load out of flight (dest=SPACECENTER)")
                && Logged("Flight", "live tree 'Decision Tree' as pending Limbo"));
            Assert.False(Logged("Flight", "CommitTreeSceneExit"));
            // The Limbo stash finalizes nothing: the snapshot is untouched.
            Assert.NotNull(tree.Recordings["a"].VesselSnapshot);
        }

        [Fact]
        public void SceneExit_SaveFileLoadDuringRevert_DoesNotTakeTheLimboStash()
        {
            AutoMerge(true);
            var (host, flow) = Flow(Tree(Rec("a", TerminalState.Orbiting)));
            SaveFileLoadIntent.Arm(50, "SPACECENTER", 0, 0);
            RevertDetector.SetPendingForTesting(RevertKind.Launch);

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.False(host.Called("StashActiveTreeAsPendingLimbo"));
            Assert.True(Logged("Flight", "CommitTreeSceneExit: stashed pending tree 'Decision Tree'"));
            Assert.Equal(PendingTreeState.Finalized, PendingTreeSlot.PendingTreeStateValue);
        }

        [Fact]
        public void SceneExit_QuitWithoutSaving_StashesLimboAndNotesTheTree()
        {
            AutoMerge(true);
            NoSaveSceneExit.FrameForTesting = () => 7;
            NoSaveSceneExit.SaveFolderForTesting = () => "unit-save";
            NoSaveSceneExit.Arm(GameScenes.MAINMENU);
            var (host, flow) = Flow(Tree(Rec("a")));

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.MAINMENU, 100);

            Assert.True(host.Called("StashActiveTreeAsPendingLimbo:100"));
            Assert.True(Logged("Flight", "FinalizeTreeOnSceneChange: quit without saving out of flight (dest=MAINMENU)"));
        }

        [Fact]
        public void SceneExit_AutoMergeOff_FinalizesAndStashesWithSnapshotsPreserved()
        {
            AutoMerge(false);
            var sub = Rec("a", TerminalState.SubOrbital);
            var tree = Tree(sub);
            var (host, flow) = Flow(tree);

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.True(Logged("Flight", "CommitTreeSceneExit (autoMerge off): stashed tree 'Decision Tree' with snapshots preserved"));
            Assert.False(Logged("Flight", "CommitTreeSceneExit: finalizing tree"));
            Assert.True(PendingTreeSlot.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, PendingTreeSlot.PendingTreeStateValue);
            // The dialog path keeps every snapshot: a non-spawnable one is not released.
            Assert.NotNull(sub.VesselSnapshot);
            Assert.False(host.Called("StashActiveTree"));
            Assert.True(host.Called("ResolveFinalizationCacheForRecording:a"));
        }

        [Fact]
        public void SceneExit_AutoMergeOn_CommitsReleasingNonSpawnableSnapshots()
        {
            AutoMerge(true);
            var sub = Rec("a", TerminalState.SubOrbital);
            var orbit = Rec("b", TerminalState.Orbiting);
            var tree = Tree(sub, orbit);
            var (host, flow) = Flow(tree);

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.True(Logged("Flight", "CommitTreeSceneExit: finalizing tree at UT=100.0"));
            Assert.True(Logged("Flight", "CommitTreeSceneExit: stashed pending tree 'Decision Tree'"));
            Assert.Equal(PendingTreeState.Finalized, PendingTreeSlot.PendingTreeStateValue);
            Assert.Null(sub.VesselSnapshot);
            Assert.NotNull(sub.GhostVisualSnapshot);
            Assert.NotNull(orbit.VesselSnapshot);
            // The commit logs the recorder state at entry and after the stash.
            Assert.Equal(2, host.Calls.FindAll(c => c == "CaptureRecorderState").Count);
        }

        [Fact]
        public void SceneExit_RewindLoadWithAutoMergeOff_CommitsLikeAutoMergeOn()
        {
            AutoMerge(false);
            var sub = Rec("a", TerminalState.SubOrbital);
            var (host, flow) = Flow(Tree(sub));
            typeof(RewindContext).GetProperty("IsRewinding", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(null, true);
            try
            {
                flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);
            }
            finally
            {
                typeof(RewindContext).GetProperty("IsRewinding", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .SetValue(null, false);
            }

            Assert.True(Logged("Flight", "CommitTreeSceneExit: stashed pending tree"));
            Assert.False(Logged("Flight", "autoMerge off"));
            Assert.Null(sub.VesselSnapshot);
        }

        [Fact]
        public void SceneExit_LiveEntry_ReadsTheUniversalTimeFromTheHost()
        {
            AutoMerge(true);
            var (host, flow) = Flow(Tree(Rec("a")));
            host.UniversalTime = 777.0;

            flow.FinalizeTreeOnSceneChange(GameScenes.SPACECENTER);

            Assert.True(Logged("Flight", "CommitTreeSceneExit: finalizing tree at UT=777.0"));
            Assert.True(Logged("RecState", "FinalizeTreeOnSceneChange:entry"));
        }

        [Fact]
        public void SceneExit_CallbackEntry_ClearsTransientStateBeforeFinalizing()
        {
            AutoMerge(true);
            var (host, flow) = Flow(Tree(Rec("a")));

            flow.FinalizeTreeOnSceneChangeForCallback(GameScenes.SPACECENTER);

            int clear = host.IndexOf("ClearSceneChangeTransientState");
            int ut = host.IndexOf("CurrentUniversalTime");
            Assert.True(clear >= 0 && ut > clear);
            Assert.True(PendingTreeSlot.HasPendingTree);
        }

        // ================= scene-exit finalize: recorders and teardown =================

        [Fact]
        public void SceneExit_TeardownFlushesAndDropsTheRecorderAndTree()
        {
            // A background recorder is left out: its Shutdown() reads Planetarium itself.
            AutoMerge(true);
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);
            host.recorder = Recorder(isRecording: false, backgrounded: false);

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.Null(host.activeTree);
            Assert.Null(host.recorder);
            Assert.True(host.Called("FlushRecorderToTreeRecording:tree-d"));
        }

        [Fact]
        public void SceneExit_NoBackgroundRecorder_NoCheckpointLine()
        {
            AutoMerge(true);
            var (host, flow) = Flow(Tree(Rec("a")));

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.SPACECENTER, 100);

            Assert.False(Logged("Checkpoint", "checkpointed all background vessels"));
            Assert.False(host.Called("FlushRecorderToTreeRecording"));
        }

        [Fact]
        public void SceneExit_UnconsumedRecoveryRequestIsClearedOnEveryPath()
        {
            var (host, flow) = Flow(Tree(Rec("a")));
            InFlightRecoveryRequest.Arm(123u, Guid.NewGuid().ToString("N"), "Vessel a", 90);
            Assert.NotNull(InFlightRecoveryRequest.Armed);

            flow.FinalizeTreeOnSceneChangeForTesting(GameScenes.FLIGHT, 100);

            Assert.Null(InFlightRecoveryRequest.Armed);
        }

        // ================= FinalizeTreeRecordings: recorder stop and flush =================

        [Fact]
        public void FinalizeTreeRecordings_BackgroundedRecorder_StopsWithoutForceStop()
        {
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);
            var rec = Recorder(isRecording: true, backgrounded: true);
            host.recorder = rec;

            flow.FinalizeTreeRecordings(tree, 100, isSceneExit: true);

            Assert.False(rec.IsRecording);
            Assert.False(Logged("Recorder", "Auto-stopped recording due to scene change"));
            Assert.True(host.Called("FlushRecorderToTreeRecording:tree-d"));
        }

        [Fact]
        public void FinalizeTreeRecordings_DestroyedActiveRecorder_PassesTheDestroyedFlagToTheActiveRecordingOnly()
        {
            var a = Rec("a");
            var b = Rec("b");
            var tree = Tree(a, b);
            var (host, flow) = Flow(tree);
            var rec = Recorder(isRecording: false, backgrounded: false);
            rec.VesselDestroyedDuringRecording = true;
            host.recorder = rec;

            flow.FinalizeTreeRecordings(tree, 100, isSceneExit: true);

            Assert.Equal(TerminalState.Destroyed, a.TerminalStateValue);
            Assert.NotEqual(TerminalState.Destroyed, b.TerminalStateValue);
        }

        [Fact]
        public void FinalizeTreeRecordings_NoRecorder_NeitherFlushesNorStops()
        {
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);

            flow.FinalizeTreeRecordings(tree, 100, isSceneExit: true);

            Assert.False(host.Called("FlushRecorderToTreeRecording"));
            Assert.True(host.Called("ResolveFinalizationCacheForRecording:a"));
        }

        // ================= in-flight commit =================

        [Fact]
        public void CommitTreeFlight_NoActiveTree_ShowsTheMessageAndDoesNothing()
        {
            var host = new FakeMergeFlowHost();
            var flow = new ParsekFlightMergeFlow(host);
            string shown = null;
            ParsekLog.ScreenMessageSinkForTesting = (m, d) => shown = m;

            flow.CommitTreeFlight();

            Assert.Equal("No active tree to commit", shown);
            Assert.False(host.Called("CurrentUniversalTime"));
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void CommitTreeFlight_CommitsSpawnsLeavesAndClearsTheSession()
        {
            var active = Rec("a", TerminalState.Orbiting);
            var tree = Tree(active);
            var (host, flow) = Flow(tree);
            host.recorder = Recorder(isRecording: false, backgrounded: false, vesselPid: 4242u);
            host.backgroundRecorder = new BackgroundRecorder(tree);
            host.lastPlaybackIndex = 9;
            host.UniversalTime = 300;

            flow.CommitTreeFlight();

            Assert.True(active.VesselSpawned);
            Assert.Equal(4242u, active.SpawnedVesselPersistentId);
            Assert.Equal(active.Points.Count - 1, active.LastAppliedResourceIndex);
            Assert.False(host.Called("ActiveVesselPersistentIdOrZero"));
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == "a");
            Assert.Same(tree, host.SpawnedLeavesTree);
            Assert.Equal("a", host.SpawnedLeavesActiveId);
            Assert.Null(host.activeTree);
            Assert.Null(host.recorder);
            Assert.Null(host.backgroundRecorder);
            Assert.Equal(0, host.lastPlaybackIndex);
            Assert.True(Logged("Flight", "CommitTreeFlight: starting tree commit at UT=300.0"));
            Assert.True(host.Called("Log:CommitTreeFlight: committed tree \"Decision Tree\""));
        }

        [Fact]
        public void CommitTreeFlight_NoRecorder_StampsTheActiveVesselPid()
        {
            var active = Rec("a", TerminalState.Orbiting);
            var (host, flow) = Flow(Tree(active));
            host.ActiveVesselPid = 777u;

            flow.CommitTreeFlight();

            Assert.True(host.Called("ActiveVesselPersistentIdOrZero"));
            Assert.Equal(777u, active.SpawnedVesselPersistentId);
        }

        [Fact]
        public void CommitTreeFlight_ReFlySessionLive_SkipsTheOptimizationPass()
        {
            var active = Rec("a", TerminalState.Orbiting);
            var (host, flow) = Flow(Tree(active));
            var scenario = (ParsekScenario)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ParsekScenario));
            scenario.ActiveReFlySessionMarker = new ReFlySessionMarker { SessionId = "sess-unit" };
            ParsekScenario.SetInstanceForTesting(scenario);

            flow.CommitTreeFlight();

            Assert.True(Logged("Flight", "CommitTreeFlight: optimization pass skipped reason="));
            Assert.False(Logged("Flight", "CommitTreeFlight: optimization pass ran"));
            Assert.Equal("a", host.SpawnedLeavesActiveId);
        }

        [Fact]
        public void CommitTreeFlight_NoReFly_RunsTheOptimizationPass()
        {
            var (host, flow) = Flow(Tree(Rec("a", TerminalState.Orbiting)));

            flow.CommitTreeFlight();

            Assert.True(Logged("Flight", "CommitTreeFlight: optimization pass ran committed="));
        }

        [Fact]
        public void OnTreeCommittedFromMergeDialog_ReEvaluatesGhostChains()
        {
            var (host, flow) = Flow(Tree(Rec("a")));

            flow.OnTreeCommittedFromMergeDialog(host.activeTree);

            Assert.Equal(new List<string> { "EvaluateAndApplyGhostChains" }, host.Calls);
            Assert.True(Logged("GhostMap", "Tree committed from merge dialog - re-evaluating ghost chains"));
        }

        // ================= auto-discard teardown =================

        [Fact]
        public void AutoDiscard_WithMessage_ShowsItAndTearsTheSessionDown()
        {
            var tree = Tree(Rec("a"));
            var (host, flow) = Flow(tree);
            host.recorder = Recorder(isRecording: false, backgrounded: false);
            host.backgroundRecorder = new BackgroundRecorder(tree);

            flow.AutoDiscardIdleActiveTree("unit idle");

            Assert.Equal(new List<string> { "Recording discarded - idle on pad" }, host.ScreenMessages);
            Assert.True(host.Called("ClearSceneChangeTransientState"));
            Assert.Null(host.activeTree);
            Assert.Null(host.recorder);
            Assert.Null(host.backgroundRecorder);
            Assert.Null(Patches.PhysicsFramePatch.ActiveRecorder);
            Assert.True(Logged("Flight", "AutoDiscardActiveTreeCore: discarding live tree reason='unit idle' " +
                "ledgerRecalcReason='suppressed-scene-exit-discard'"));
        }

        [Fact]
        public void AutoDiscard_EmptyMessage_ShowsNothing()
        {
            var (host, flow) = Flow(Tree(Rec("a")));

            flow.AutoDiscardActiveTreeWithMessage("unit", "", "unit-recalc", reapSidecars: false);

            Assert.Empty(host.ScreenMessages);
            Assert.Null(host.activeTree);
        }

        [Fact]
        public void AutoDiscard_RecorderNotRecording_DoesNotForceStop()
        {
            var (host, flow) = Flow(Tree(Rec("a")));
            host.recorder = Recorder(isRecording: false, backgrounded: false);

            flow.AutoDiscardActiveTreeWithMessage("unit", null, "unit-recalc", reapSidecars: false);

            Assert.False(Logged("Recorder", "Auto-stopped recording due to scene change"));
            Assert.Null(host.recorder);
        }

        // ================= idle-on-pad and live recording lookup =================

        [Fact]
        public void IdleOnPad_RestoreInProgress_SkipsBeforeFlushing()
        {
            var (host, flow) = Flow(Tree(Rec("a")));
            ParsekFlight.restoringActiveTree = true;

            Assert.False(flow.IsActiveTreeIdleOnPad());
            Assert.False(host.Called("FlushRecorderIntoActiveTreeForSerialization"));
            Assert.True(Logged("Flight", "IsActiveTreeIdleOnPad: skipped - restore coroutine in progress"));
        }

        [Fact]
        public void IdleOnPad_FlushesTheLiveRecorderBeforeTheWalk()
        {
            var rec = Rec("a");
            rec.MaxDistanceFromLaunch = 5000;
            var (host, flow) = Flow(Tree(rec));

            Assert.False(flow.IsActiveTreeIdleOnPad());
            Assert.True(host.Called("FlushRecorderIntoActiveTreeForSerialization"));
        }

        [Fact]
        public void IdleOnPad_NoTree_NoFlush()
        {
            var host = new FakeMergeFlowHost();
            Assert.False(new ParsekFlightMergeFlow(host).IsActiveTreeIdleOnPad());
            Assert.Empty(host.Calls);
        }

        [Theory]
        [InlineData(true, 55u, 55u, "a")]      // recording, bound to the vessel: the active id
        [InlineData(false, 55u, 55u, null)]    // recorder not recording
        [InlineData(true, 56u, 55u, null)]     // recorder bound to another vessel
        [InlineData(true, 55u, 57u, null)]     // the active recording is another vessel's
        public void LiveRecordingIdForVessel_NeedsALiveRecorderBoundToTheVessel(
            bool isRecording, uint recorderPid, uint recordingPid, string expected)
        {
            var a = Rec("a");
            a.VesselPersistentId = recordingPid;
            var (host, flow) = Flow(Tree(a));
            host.recorder = Recorder(isRecording, backgrounded: true, vesselPid: recorderPid);

            Assert.Equal(expected, flow.LiveRecordingIdForVessel(55u));
        }

        [Fact]
        public void LiveRecordingIdForVessel_ZeroPidOrNoRecorder_Null()
        {
            var a = Rec("a");
            a.VesselPersistentId = 55u;
            var (host, flow) = Flow(Tree(a));
            Assert.Null(flow.LiveRecordingIdForVessel(55u));
            host.recorder = Recorder(true, backgrounded: true, vesselPid: 55u);
            Assert.Null(flow.LiveRecordingIdForVessel(0u));
            Assert.Equal("a", flow.LiveRecordingIdForVessel(55u));
        }
    }
}
