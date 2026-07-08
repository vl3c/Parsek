using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M6 "Record Supply Run" helper: the pure
    /// <see cref="RouteRunPrompt.ShouldPrompt"/> decision table (eligible
    /// prompts once; dismissed / already-prompted / near-miss / batch /
    /// restore never prompt), the <see cref="RouteRunPrompt.NotifyTreeCommittedCore"/>
    /// commit hook against the real <see cref="RouteCandidateFinder"/>
    /// classification, and the RouteStore-owned prompted-tree-id persistence
    /// (sparse PROMPTED_ROUTE_CANDIDATES node: round-trip, empty-set byte
    /// identity, stale-id sweep). Touches RouteStore + RecordingStore +
    /// ParsekLog shared static state, so the class is Sequential with full
    /// resets in constructor and Dispose (the RouteCandidateDismissTests
    /// pattern).
    /// </summary>
    [Collection("Sequential")]
    public class RouteRunPromptTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();

        public RouteRunPromptTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) => screenMessages.Add(msg);
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RouteRunPrompt.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteRunPrompt.ResetForTesting();
            RouteStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // ShouldPrompt: pure decision table
        // ------------------------------------------------------------------

        private static readonly string[] Empty = new string[0];

        // catches: the happy path not prompting (an eligible first commit is
        // the whole point of the helper).
        [Fact]
        public void ShouldPrompt_EligibleFirstCommit_True()
        {
            bool ok = RouteRunPrompt.ShouldPrompt(
                "t1", true, Empty, Empty, false, false, out string reason);

            Assert.True(ok);
            Assert.Equal("eligible-first-commit", reason);
        }

        // catches: a null/empty tree id slipping through into the prompted set.
        [Fact]
        public void ShouldPrompt_NoTreeId_False()
        {
            Assert.False(RouteRunPrompt.ShouldPrompt(
                null, true, Empty, Empty, false, false, out string r1));
            Assert.Equal("no-tree-id", r1);
            Assert.False(RouteRunPrompt.ShouldPrompt(
                "", true, Empty, Empty, false, false, out string r2));
            Assert.Equal("no-tree-id", r2);
        }

        // catches: prompts firing during an in-game test batch (tests commit
        // trees constantly; a prompt would burn the tree's one shot AND leave
        // UI state behind).
        [Fact]
        public void ShouldPrompt_TestBatchActive_False()
        {
            Assert.False(RouteRunPrompt.ShouldPrompt(
                "t1", true, Empty, Empty, true, false, out string reason));
            Assert.Equal("test-batch-active", reason);
        }

        // catches: prompts firing inside a restore window (the commit is
        // machinery, not a player flight ending).
        [Fact]
        public void ShouldPrompt_RestoreWindowActive_False()
        {
            Assert.False(RouteRunPrompt.ShouldPrompt(
                "t1", true, Empty, Empty, false, true, out string reason));
            Assert.Equal("restore-window-active", reason);
        }

        // catches: a player-dismissed tree being re-prompted (dismiss means
        // "stop suggesting this tree", full stop).
        [Fact]
        public void ShouldPrompt_Dismissed_False()
        {
            Assert.False(RouteRunPrompt.ShouldPrompt(
                "t1", true, Empty, new[] { "t1" }, false, false, out string reason));
            Assert.Equal("dismissed", reason);
        }

        // catches: a second commit of the same tree re-prompting (the
        // at-most-once contract).
        [Fact]
        public void ShouldPrompt_AlreadyPrompted_False()
        {
            Assert.False(RouteRunPrompt.ShouldPrompt(
                "t1", true, new[] { "t1" }, Empty, false, false, out string reason));
            Assert.Equal("already-prompted", reason);
        }

        // catches: a near-miss (ineligible / not sealed) prompting.
        [Fact]
        public void ShouldPrompt_NotEligible_False()
        {
            Assert.False(RouteRunPrompt.ShouldPrompt(
                "t1", false, Empty, Empty, false, false, out string reason));
            Assert.Equal("not-eligible-candidate", reason);
        }

        // ------------------------------------------------------------------
        // NotifyTreeCommittedCore: real finder classification + one-shot arm
        // ------------------------------------------------------------------

        // catches: an eligible committed tree not arming the banner, not
        // persisting the prompted id, or not announcing via ScreenMessage.
        [Fact]
        public void Notify_EligibleTree_ArmsOnceAndPersists()
        {
            RecordingTree tree = BuildEligibleTree("t-arm");
            tree.TreeName = "Mun Fuel Run";

            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);

            Assert.True(RouteRunPrompt.HasPendingPrompt);
            Assert.Equal("t-arm", RouteRunPrompt.PendingPromptTreeId);
            Assert.Equal("Mun Fuel Run", RouteRunPrompt.PendingPromptLabel);
            Assert.True(RouteStore.IsCandidatePrompted("t-arm"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("[Route]")
                && l.Contains("RouteRunPrompt armed")
                && l.Contains("tree=t-arm")
                && l.Contains("name='Mun Fuel Run'"));
            Assert.Contains(screenMessages, m => m.Contains("qualifies as a Supply Route"));
        }

        // catches: a second commit of the same tree re-prompting after the
        // player already saw (and cleared) the first banner.
        [Fact]
        public void Notify_SecondCommitOfSameTree_NeverRePrompts()
        {
            RecordingTree tree = BuildEligibleTree("t-once");
            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);
            RouteRunPrompt.ClearPendingPrompt("test-acted");
            screenMessages.Clear();

            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);

            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.Empty(screenMessages);
            Assert.Contains(logLines, l =>
                l.Contains("RouteRunPrompt skipped") && l.Contains("reason=already-prompted"));
        }

        // catches: a dismissed tree prompting (the finder already skips it,
        // and the predicate must also refuse independently).
        [Fact]
        public void Notify_DismissedTree_NeverPrompts()
        {
            RecordingTree tree = BuildEligibleTree("t-dis");
            RouteStore.DismissCandidateTree("t-dis", "Dismissed Run");

            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);

            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.False(RouteStore.IsCandidatePrompted("t-dis"));
            Assert.Contains(logLines, l =>
                l.Contains("RouteRunPrompt skipped") && l.Contains("reason=dismissed"));
        }

        // catches: a near-miss commit (here: a not-fully-sealed tree, the
        // common straight-after-commit shape with open re-fly slots)
        // prompting, OR burning the tree's one shot - a later sealed commit
        // must still be able to prompt.
        [Fact]
        public void Notify_NearMissTree_NoPromptAndNoShotBurned()
        {
            RecordingTree tree = BuildEligibleTree("t-nm");
            tree.Recordings["mid"].MergeState = MergeState.CommittedProvisional;

            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);

            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.False(RouteStore.IsCandidatePrompted("t-nm"));
            Assert.Contains(logLines, l =>
                l.Contains("RouteRunPrompt skipped") && l.Contains("reason=not-eligible-candidate"));

            // Seal and re-commit: the shot was not burned, so it prompts now.
            tree.Recordings["mid"].MergeState = MergeState.Immutable;
            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);
            Assert.True(RouteRunPrompt.HasPendingPrompt);
        }

        // catches: batch / restore gates leaking a prompt or burning the shot
        // (a batch-committed tree must still prompt on a later player commit).
        [Fact]
        public void Notify_BatchOrRestoreGate_NoPromptAndNoShotBurned()
        {
            RecordingTree tree = BuildEligibleTree("t-gate");

            RouteRunPrompt.NotifyTreeCommittedCore(tree, testBatchActive: true, restoreWindowActive: false);
            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.False(RouteStore.IsCandidatePrompted("t-gate"));

            RouteRunPrompt.NotifyTreeCommittedCore(tree, testBatchActive: false, restoreWindowActive: true);
            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.False(RouteStore.IsCandidatePrompted("t-gate"));

            // Gates lifted: prompts normally.
            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);
            Assert.True(RouteRunPrompt.HasPendingPrompt);
        }

        // catches: a null tree throwing out of the commit path.
        [Fact]
        public void Notify_NullTree_QuietNoOp()
        {
            RouteRunPrompt.NotifyTreeCommittedCore(null, false, false);

            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.Contains(logLines, l =>
                l.Contains("RouteRunPrompt skipped") && l.Contains("reason=no-tree-id"));
        }

        // catches: ClearPendingPromptIfTree clearing someone else's banner.
        [Fact]
        public void ClearPendingPromptIfTree_OnlyMatchingTree()
        {
            RecordingTree tree = BuildEligibleTree("t-clear");
            RouteRunPrompt.NotifyTreeCommittedCore(tree, false, false);

            RouteRunPrompt.ClearPendingPromptIfTree("t-other", "route-created");
            Assert.True(RouteRunPrompt.HasPendingPrompt);

            RouteRunPrompt.ClearPendingPromptIfTree("t-clear", "route-created");
            Assert.False(RouteRunPrompt.HasPendingPrompt);
            Assert.Contains(logLines, l =>
                l.Contains("RouteRunPrompt cleared")
                && l.Contains("tree=t-clear")
                && l.Contains("reason=route-created"));
        }

        // ------------------------------------------------------------------
        // Prompted-set persistence: sparse node + round-trip + sweep
        // ------------------------------------------------------------------

        // catches: MarkCandidatePrompted mutating silently or missing the
        // dup/null no-op contract.
        [Fact]
        public void MarkCandidatePrompted_HappyDupAndNull()
        {
            Assert.True(RouteStore.MarkCandidatePrompted("t-p", "P Run"));
            Assert.True(RouteStore.IsCandidatePrompted("t-p"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("Candidate prompted")
                && l.Contains("treeId=t-p") && l.Contains("name='P Run'"));

            Assert.False(RouteStore.MarkCandidatePrompted("t-p", "P Run"));
            Assert.False(RouteStore.MarkCandidatePrompted(null, "x"));
            Assert.False(RouteStore.MarkCandidatePrompted("", "x"));
        }

        // catches: an empty prompted set leaking a PROMPTED_ROUTE_CANDIDATES
        // node into the save (byte identity for saves without prompts), or a
        // stale node from a prior save surviving an empty-set save.
        [Fact]
        public void SaveRoutesTo_EmptyPromptedSet_WritesNothingAndStripsStaleNode()
        {
            var parent = new ConfigNode("SCENARIO");
            parent.AddNode("PROMPTED_ROUTE_CANDIDATES").AddValue("treeId", "stale");

            RouteStore.SaveRoutesTo(parent);

            Assert.Null(parent.GetNode("PROMPTED_ROUTE_CANDIDATES"));
        }

        // catches: a save written WITHOUT the node not loading to an empty set
        // (old saves must behave as "nothing ever prompted").
        [Fact]
        public void Load_SaveWithoutPromptedNode_EmptySet()
        {
            RouteStore.MarkCandidatePrompted("t-leftover", "Leftover");
            var parent = new ConfigNode("SCENARIO"); // no PROMPTED node

            RouteStore.LoadRoutesFrom(parent);

            Assert.Empty(RouteStore.PromptedCandidateTreeIds);
        }

        // catches: the prompted set not surviving a save/load round-trip
        // (wholesale replace semantics, sibling-of-ROUTES load order).
        [Fact]
        public void SaveLoad_RoundTrip_PersistsPromptedIds()
        {
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-a" });
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-b" });
            RouteStore.MarkCandidatePrompted("t-a", "A");
            RouteStore.MarkCandidatePrompted("t-b", "B");

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);

            ConfigNode promptedNode = parent.GetNode("PROMPTED_ROUTE_CANDIDATES");
            Assert.NotNull(promptedNode);
            Assert.Equal(2, promptedNode.GetValues("treeId").Length);

            RouteStore.MarkCandidatePrompted("t-leftover", "Leftover");
            RouteStore.LoadRoutesFrom(parent);

            Assert.True(RouteStore.IsCandidatePrompted("t-a"));
            Assert.True(RouteStore.IsCandidatePrompted("t-b"));
            Assert.False(RouteStore.IsCandidatePrompted("t-leftover"));
        }

        // catches: stale prompted ids of deleted / pruned trees lingering
        // forever (and the sweep Verbose count going missing).
        [Fact]
        public void LoadRoutesFrom_SweepsStalePromptedIds()
        {
            RecordingStore.AddCommittedTreeForTesting(new RecordingTree { Id = "t-live" });
            RouteStore.MarkCandidatePrompted("t-live", "Live");
            RouteStore.MarkCandidatePrompted("t-gone", "Gone");

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);

            RouteStore.LoadRoutesFrom(parent);

            Assert.True(RouteStore.IsCandidatePrompted("t-live"));
            Assert.False(RouteStore.IsCandidatePrompted("t-gone"));
            Assert.Contains(logLines, l =>
                l.Contains("SweepStalePromptedCandidates")
                && l.Contains("swept=1")
                && l.Contains("kept=1"));
        }

        // catches: ResetForTesting leaking the prompted set into the next test.
        [Fact]
        public void ResetForTesting_ClearsPromptedSet()
        {
            RouteStore.MarkCandidatePrompted("t-reset", "Reset");
            Assert.True(RouteStore.IsCandidatePrompted("t-reset"));

            RouteStore.ResetForTesting();

            Assert.False(RouteStore.IsCandidatePrompted("t-reset"));
            Assert.Empty(RouteStore.PromptedCandidateTreeIds);
        }

        // ------------------------------------------------------------------
        // Fixture: sealed eligible tree (the RouteCandidateDismissTests shape).
        // ------------------------------------------------------------------

        private static RecordingTree BuildEligibleTree(string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = "root",
                ActiveRecordingId = null
            };
            tree.BranchPoints = new List<BranchPoint>
            {
                new BranchPoint { Id = "bp-dock", ParentRecordingIds = new List<string> { "root" } },
                new BranchPoint { Id = "bp-undock", ParentRecordingIds = new List<string> { "mid" } }
            };
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "root",
                TreeId = treeId,
                ParentBranchPointId = null,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad"
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "mid",
                TreeId = treeId,
                ParentBranchPointId = "bp-dock",
                RouteConnectionWindows = new List<RouteConnectionWindow> { BuildDeliveryWindow() }
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "post",
                TreeId = treeId,
                ParentBranchPointId = "bp-undock"
            });
            return tree;
        }

        private static RouteConnectionWindow BuildDeliveryWindow()
        {
            return new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(30.0, 100.0),
                DockEndpointResources = Manifest(0.0, 200.0),
                UndockEndpointResources = Manifest(50.0, 200.0),
                EndpointAtDock = new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
                TransferEndpointSituation = 4
            };
        }

        private static Dictionary<string, ResourceAmount> Manifest(double amount, double maxAmount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = amount, maxAmount = maxAmount }
            };
        }
    }
}
