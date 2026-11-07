using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A rewind's own Space Center load never waits on a merge dialog (owner ruling
    /// 2026-11-01). EVA-16 (auto-merge off) rewound with a live resumed clone and the
    /// rewind's LoadScene was swallowed by the pre-transition merge dialog after the
    /// rewind save had already been written over persistent.sfs; the run then hit
    /// rewind-timeout.
    /// </summary>
    [Collection("Sequential")]
    public class RewindSceneExitNeverAsksTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes priorScene;

        public RewindSceneExitNeverAsksTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            SceneExitInterceptor.ResetTestOverrides();
            priorScene = HighLogic.LoadedScene;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            SceneExitInterceptor.ResetTestOverrides();
            RewindContext.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void BeginRewind()
            => RewindContext.BeginRewind(100.0, default(BudgetSummary), 0, 0, 0f);

        private static RecordingTree FinalizedPendingTree(string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Pending " + treeId,
                RootRecordingId = treeId + "_root",
                ActiveRecordingId = treeId + "_root",
            };
            var rec = new Recording
            {
                RecordingId = treeId + "_root",
                TreeId = treeId,
                VesselName = "Pending",
                TerminalStateValue = TerminalState.Landed,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 20, bodyName = "Kerbin" });
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        // ---- pure ----------------------------------------------------------

        [Theory]
        [InlineData(true, GameScenes.SPACECENTER, true)]
        [InlineData(false, GameScenes.SPACECENTER, false)]
        [InlineData(true, GameScenes.TRACKSTATION, false)]
        [InlineData(true, GameScenes.MAINMENU, false)]
        [InlineData(true, GameScenes.FLIGHT, false)]
        public void IsRewindSceneExit_OnlyTheRewindsOwnSpaceCenterLoad(
            bool isRewinding, GameScenes destination, bool expected)
        {
            Assert.Equal(expected, SceneExitInterceptor.IsRewindSceneExit(destination, isRewinding));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void ResolveSceneExitAutoMerge_RewindForcesAutoMerge(
            bool isAutoMerge, bool rewindSceneExit, bool expected)
        {
            Assert.Equal(expected,
                SceneExitInterceptor.ResolveSceneExitAutoMerge(isAutoMerge, rewindSceneExit));
        }

        // ---- live wrappers -------------------------------------------------

        [Fact]
        public void LiveAutoMerge_IsForcedOnlyForTheRewindLoad()
        {
            Assert.False(SceneExitInterceptor.IsAutoMergeForSceneExitLive(GameScenes.SPACECENTER, "test"));

            BeginRewind();
            Assert.True(SceneExitInterceptor.IsAutoMergeForSceneExitLive(GameScenes.SPACECENTER, "test"));
            Assert.False(SceneExitInterceptor.IsAutoMergeForSceneExitLive(GameScenes.TRACKSTATION, "test"));
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("test: rewind scene load dest=SPACECENTER")
                && l.Contains("a rewind never waits on a merge dialog"));
        }

        [Fact]
        public void Prefix_RewindLoad_FinalizedPendingTree_ShowsNoDialog()
        {
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinalizedPendingTree("pend"), PendingTreeState.Finalized);
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            BeginRewind();

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
        }

        [Fact]
        public void Prefix_NotRewinding_SameStateStillAsks()
        {
            // Control: auto-merge off without a rewind keeps today's dialog.
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinalizedPendingTree("pend"), PendingTreeState.Finalized);
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.False(allowStockLoad);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, shown);
        }

        [Fact]
        public void Prefix_RewindLoad_SwitchSessionWithNoLiveTree_ClearsTheSessionAndShowsNoDialog()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            var committed = FinalizedPendingTree("committed");
            TreeCommitService.CommitTree(committed);
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = "committed",
                ActiveSegmentRecordingId = "committed_root",
                SwitchUT = 15.0,
                PreSessionBranchPointIds = new List<string>(),
            });
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            BeginRewind();

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.Null(scenario.ActiveSwitchSegmentSession);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("rewind scene load with switch-segment session")
                && l.Contains("clearing the session, no dialog"));
        }

        [Fact]
        public void Prefix_NotRewinding_SwitchSessionWithNoLiveTree_StillAsks()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            TreeCommitService.CommitTree(FinalizedPendingTree("committed"));
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = "committed",
                ActiveSegmentRecordingId = "committed_root",
                SwitchUT = 15.0,
                PreSessionBranchPointIds = new List<string>(),
            });
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            HighLogic.LoadedScene = GameScenes.FLIGHT;

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.False(allowStockLoad);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, shown);
            Assert.NotNull(scenario.ActiveSwitchSegmentSession);
        }

        // ---- source gate ---------------------------------------------------

        [Fact]
        public void EverySceneExitAutoMergeReadGoesThroughTheRewindAwareHelper()
        {
            // The live-tree dialog gate and FinalizeTreeOnSceneChange need a live ParsekFlight,
            // so no unit test drives them; this pins that neither reads the raw setting.
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek"));
            string interceptor = SourceScanText.StripCommentsAndMaskLiterals(
                ParsekFlightSourceFiles.ReadText(Path.Combine(root, "SceneExitInterceptor.cs")));
            string flight = SourceScanText.StripCommentsAndMaskLiterals(
                ParsekFlightSourceFiles.ReadText(Path.Combine(root, "ParsekFlight.cs")));

            Assert.Equal(1, Regex.Matches(interceptor, @"ParsekScenario\.IsAutoMerge\b").Count);
            int helper = interceptor.IndexOf(
                "internal static bool IsAutoMergeForSceneExitLive(", StringComparison.Ordinal);
            int raw = interceptor.IndexOf("ParsekScenario.IsAutoMerge", StringComparison.Ordinal);
            Assert.True(helper >= 0 && raw > helper,
                "the one raw auto-merge read in SceneExitInterceptor is inside IsAutoMergeForSceneExitLive");
            Assert.Equal(3, Regex.Matches(interceptor, @"=\s*IsAutoMergeForSceneExitLive\(").Count);
            Assert.Contains("else if (!SceneExitInterceptor.IsAutoMergeForSceneExitLive(", flight);
        }
    }
}
