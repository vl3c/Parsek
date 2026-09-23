using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// FRESH-LAUNCH-JOINS-RESTORED-COMMITTED-TREE: a FLIGHT->FLIGHT launch made within
    /// the vessel-switch freshness window of flight-scene entry read as a vessel switch,
    /// because the scene's INITIAL focus armed the switch flag. The stashed tree (a
    /// committed tree's restore clone) was then reinstalled on the fresh rollout and the
    /// launch recorded inside it. Guards pinned here: the flag arms only after
    /// onFlightReady (OnLoad clears readiness, OnFlightReady sets it), and the
    /// vessel-switch restore refuses a fresh-rollout active vessel before it pops the
    /// tree, reverting the stash to a plain Limbo tree.
    /// </summary>
    [Collection("Sequential")]
    public class FreshLaunchVesselSwitchGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool savedReady;
        private readonly bool savedPending;

        public FreshLaunchVesselSwitchGuardTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            savedReady = ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting;
            savedPending = ParsekScenario.VesselSwitchPendingForTesting;
            ParsekScenario.VesselSwitchPendingForTesting = false;
        }

        public void Dispose()
        {
            ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting = savedReady;
            ParsekScenario.VesselSwitchPendingForTesting = savedPending;
            ParsekLog.ResetTestOverrides();
        }

        // ---------------- arming ----------------

        [Fact]
        public void TryArm_BeforeFlightReady_DoesNotArmAndLogsIgnored()
        {
            // The scene-entry initial focus fires after OnLoad and before onFlightReady.
            ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting = false;

            bool armed = ParsekScenario.TryArmVesselSwitchFlag("", "Kerbal X", 7621);

            Assert.False(armed);
            Assert.False(ParsekScenario.VesselSwitchPendingForTesting);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Vessel switch ignored") && l.Contains("frame=7621"));
            Assert.DoesNotContain(logLines, l => l.Contains("Vessel switch detected"));
        }

        [Fact]
        public void TryArm_AfterFlightReady_ArmsWithFrame()
        {
            // Mirror direction: a switch after onFlightReady still arms, so the
            // FLIGHT->FLIGHT dispatch keeps seeing a real switch.
            ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting = true;

            bool armed = ParsekScenario.TryArmVesselSwitchFlag("Station", "Probe", 9000);

            Assert.True(armed);
            Assert.True(ParsekScenario.VesselSwitchPendingForTesting);
            Assert.Equal(9000, ParsekScenario.VesselSwitchPendingFrameForTesting);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Vessel switch detected") && l.Contains("frame=9000"));
        }

        [Fact]
        public void NoteFlightSceneReady_ThenClear_GatesArming()
        {
            ParsekScenario.FlightSceneReadyForVesselSwitchFlagForTesting = false;
            ParsekScenario.NoteFlightSceneReadyForVesselSwitchFlag();
            ParsekScenario.NoteFlightSceneReadyForVesselSwitchFlag();
            Assert.True(ParsekScenario.TryArmVesselSwitchFlag("a", "b", 1));
            Assert.Single(logLines.FindAll(l =>
                l.Contains("Vessel-switch flag armed for this flight scene")));

            ParsekScenario.VesselSwitchPendingForTesting = false;
            ParsekScenario.ClearFlightSceneReadyForVesselSwitchFlag();
            Assert.False(ParsekScenario.TryArmVesselSwitchFlag("a", "b", 2));
            Assert.False(ParsekScenario.VesselSwitchPendingForTesting);
        }

        // ---------------- refusal predicate ----------------

        [Fact]
        public void ShouldRefuseVesselSwitchRestore_ActiveVesselIsFreshRollout_Refuses()
        {
            Assert.True(ParsekFlight.ShouldRefuseVesselSwitchRestoreForFreshRollout(
                activeVesselPid: 760196917u, sceneEntryFreshRolloutPid: 760196917u));
        }

        [Fact]
        public void ShouldRefuseVesselSwitchRestore_ResumedSceneOrOtherVessel_DoesNotRefuse()
        {
            Assert.False(ParsekFlight.ShouldRefuseVesselSwitchRestoreForFreshRollout(
                activeVesselPid: 3620499050u, sceneEntryFreshRolloutPid: 0u));
            Assert.False(ParsekFlight.ShouldRefuseVesselSwitchRestoreForFreshRollout(
                activeVesselPid: 3620499050u, sceneEntryFreshRolloutPid: 760196917u));
        }

        // ---------------- pre-transition revert ----------------

        private static RecordingTree MakeStationTree()
        {
            var tree = new RecordingTree
            {
                Id = "tree_station", TreeName = "Kerbal X",
                RootRecordingId = "rec_tip", ActiveRecordingId = "rec_tip",
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "rec_tip", VesselName = "Kerbal X", TreeId = "tree_station",
                VesselPersistentId = 3620499050u,
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "rec_bg", VesselName = "Kerbal X Probe", TreeId = "tree_station",
                VesselPersistentId = 1223410921u,
            });
            tree.BackgroundMap[1223410921u] = "rec_bg";
            return tree;
        }

        [Fact]
        public void RevertPreTransition_RestoresMovedRecordingOnly()
        {
            var tree = MakeStationTree();
            Assert.True(ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 3620499050u));
            Assert.Null(tree.ActiveRecordingId);
            Assert.Equal(2, tree.BackgroundMap.Count);

            Assert.True(ParsekFlight.TryRevertPreTransitionForVesselSwitch(tree, out string rec));

            Assert.Equal("rec_tip", rec);
            Assert.Equal("rec_tip", tree.ActiveRecordingId);
            Assert.Single(tree.BackgroundMap);
            Assert.Equal("rec_bg", tree.BackgroundMap[1223410921u]);
            // One-shot: the memo is consumed.
            tree.ActiveRecordingId = null;
            Assert.False(ParsekFlight.TryRevertPreTransitionForVesselSwitch(tree, out _));
        }

        [Fact]
        public void RevertPreTransition_OutsiderChainOrChangedMap_LeavesTreeUntouched()
        {
            // Outsider chain: nothing was active at stash time, nothing to restore.
            var outsider = MakeStationTree();
            outsider.ActiveRecordingId = null;
            Assert.False(ParsekFlight.ApplyPreTransitionForVesselSwitch(outsider, 0));
            Assert.False(ParsekFlight.TryRevertPreTransitionForVesselSwitch(outsider, out _));
            Assert.Null(outsider.ActiveRecordingId);

            // The BackgroundMap entry changed after the stash: do not guess.
            var changed = MakeStationTree();
            ParsekFlight.ApplyPreTransitionForVesselSwitch(changed, 3620499050u);
            changed.BackgroundMap[3620499050u] = "rec_bg";
            Assert.False(ParsekFlight.TryRevertPreTransitionForVesselSwitch(changed, out _));
            Assert.Null(changed.ActiveRecordingId);
        }

        // ---------------- source-order gates ----------------
        // The OnLoad clear, the OnFlightReady arm and the coroutine refusal live on
        // Unity-lifecycle paths with no headless seam. These cells read comment-blanked,
        // literal-masked source anchored on each method's declaration.

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

        private static int IndexOrFail(string body, string needle)
        {
            int i = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(i >= 0, "needle not found in body: " + needle);
            return i;
        }

        [Fact]
        public void SourceGate_OnLoadClearsReadinessBeforeItsBody()
        {
            string body = MethodBody(ReadSource("ParsekScenario.cs"),
                "public override void OnLoad(ConfigNode node)");
            int clear = IndexOrFail(body, "ClearFlightSceneReadyForVesselSwitchFlag();");
            Match tryBlock = Regex.Match(body, @"\btry\s*\{");
            Assert.True(tryBlock.Success, "OnLoad try block not found");
            int firstTry = tryBlock.Index;
            Assert.True(clear < firstTry,
                "OnLoad must clear flight-scene readiness before its guarded body");
        }

        [Fact]
        public void SourceGate_OnVesselSwitchingRoutesThroughTryArm()
        {
            string body = MethodBody(ReadSource("ParsekScenario.cs"),
                "private void OnVesselSwitching(Vessel from, Vessel to)");
            IndexOrFail(body, "TryArmVesselSwitchFlag(");
            Assert.DoesNotContain("vesselSwitchPending = true", body);
        }

        [Fact]
        public void SourceGate_OnFlightReadyArmsBeforeTheRestoreEarlyReturn()
        {
            string body = MethodBody(ReadSource("ParsekFlight.cs"), "void OnFlightReady()");
            int arm = IndexOrFail(body, "ParsekScenario.NoteFlightSceneReadyForVesselSwitchFlag();");
            int earlyReturn = IndexOrFail(body, "if (restoringActiveTree)");
            Assert.True(arm < earlyReturn,
                "OnFlightReady must mark the scene ready before any early return");
        }

        [Fact]
        public void SourceGate_SwitchRestoreRefusesFreshRolloutBeforePop()
        {
            string body = MethodBody(ReadSource("ParsekFlight.cs"),
                "IEnumerator RestoreActiveTreeFromPendingForVesselSwitch()");
            int refuse = IndexOrFail(body, "ShouldRefuseVesselSwitchRestoreForFreshRollout(");
            int revert = IndexOrFail(body, "TryRevertPreTransitionForVesselSwitch(");
            int convert = IndexOrFail(body, "RecordingStore.ConvertPendingVesselSwitchStashToLimbo(");
            int pop = IndexOrFail(body, "RecordingStore.PopPendingTree()");
            Assert.True(refuse < revert && revert < convert && convert < pop,
                "the fresh-rollout refusal (revert + convert) must run before the tree is popped");
            // The refusal branch must leave the coroutine, not fall through to the pop.
            int yieldBreak = body.IndexOf("yield break;", convert, StringComparison.Ordinal);
            Assert.True(yieldBreak > convert && yieldBreak < pop,
                "the refusal branch must yield break before the pop");
        }
    }
}
