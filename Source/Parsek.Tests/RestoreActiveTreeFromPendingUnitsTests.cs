using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the decision units the <c>ParsekFlight.RestoreActiveTreeFromPending</c>
    /// coroutine calls between its yields. The coroutine itself reads FlightGlobals and
    /// UnityEngine.Time, so it cannot run headless; its live paths are characterized by the
    /// in-game category TreeRestoreCoroutine.
    /// </summary>
    [Collection("Sequential")]
    public class RestoreActiveTreeFromPendingUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();

        public RestoreActiveTreeFromPendingUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ScreenMessageSinkForTesting = (msg, duration) => screenMessages.Add(msg);
            RecordingStore.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            SceneEntryVesselScope.ResetForTesting();
            ParsekScenario.SetInstanceForTesting(null);
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            SceneEntryVesselScope.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ScreenMessageSinkForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private int Count(string text) => logLines.Count(l => l.Contains(text));

        private static Recording Rec(string id, string name, uint pid, string guid = null,
            string parentId = null, string treeId = "tree-restore")
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = name,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ParentRecordingId = parentId,
            };
        }

        private static RecordingTree Tree(string activeId, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree-restore",
                TreeName = "Restore Tree",
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null,
                ActiveRecordingId = activeId,
            };
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            return tree;
        }

        private static ParsekScenario InstallMarker(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static ReFlySessionMarker InPlaceMarker(string activeId, string originId,
            string treeId = "tree-restore")
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess-restore",
                TreeId = treeId,
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                InPlaceContinuation = true,
            };
        }

        // ---- ShouldRestorePendingTreeOnFlightReady --------------------------------------

        [Fact]
        public void Gate_NoPendingTree_SkipsAndLogs()
        {
            Assert.False(ParsekFlight.ShouldRestorePendingTreeOnFlightReady());
            Assert.Equal(1, Count("RestoreActiveTreeFromPending: no pending-Limbo tree, skipping"));
            Assert.Equal(0, Count("accepting Finalized pending tree"));
        }

        [Fact]
        public void Gate_LimboTree_Proceeds()
        {
            PendingTreeSlot.StashPendingTree(Tree("a", Rec("a", "A", 1)), PendingTreeState.Limbo);
            logLines.Clear();

            Assert.True(ParsekFlight.ShouldRestorePendingTreeOnFlightReady());
            Assert.Equal(0, Count("no pending-Limbo tree"));
            Assert.Equal(0, Count("accepting Finalized pending tree"));
        }

        [Fact]
        public void Gate_FinalizedTreeWithoutReFly_Skips()
        {
            PendingTreeSlot.StashPendingTree(Tree("a", Rec("a", "A", 1)), PendingTreeState.Finalized);
            logLines.Clear();

            Assert.False(ParsekFlight.ShouldRestorePendingTreeOnFlightReady());
            Assert.Equal(1, Count("RestoreActiveTreeFromPending: no pending-Limbo tree, skipping"));
        }

        [Fact]
        public void Gate_VesselSwitchLimboTree_Skips()
        {
            PendingTreeSlot.StashPendingTree(Tree("a", Rec("a", "A", 1)), PendingTreeState.LimboVesselSwitch);
            logLines.Clear();

            Assert.False(ParsekFlight.ShouldRestorePendingTreeOnFlightReady());
            Assert.Equal(1, Count("no pending-Limbo tree, skipping"));
        }

        [Fact]
        public void Gate_FinalizedTreeUnderInPlaceReFly_ProceedsAndLogsAcceptance()
        {
            PendingTreeSlot.StashPendingTree(Tree("a", Rec("a", "A", 1)), PendingTreeState.Finalized);
            InstallMarker(InPlaceMarker("fork", "a"));
            logLines.Clear();

            Assert.True(ParsekFlight.ShouldRestorePendingTreeOnFlightReady());
            Assert.Equal(1, Count(
                "RestoreActiveTreeFromPending: accepting Finalized pending tree 'Restore Tree' — active Re-Fly session"));
            Assert.Equal(0, Count("no pending-Limbo tree"));
        }

        [Fact]
        public void Gate_FinalizedTreeUnderPlaceholderReFly_Skips()
        {
            PendingTreeSlot.StashPendingTree(Tree("a", Rec("a", "A", 1)), PendingTreeState.Finalized);
            var marker = InPlaceMarker("fork", "a");
            marker.InPlaceContinuation = false;
            InstallMarker(marker);
            logLines.Clear();

            Assert.False(ParsekFlight.ShouldRestorePendingTreeOnFlightReady());
            Assert.Equal(1, Count("no pending-Limbo tree, skipping"));
        }

        // ---- LogRewindInvokeContextWaitOutcome ------------------------------------------

        [Fact]
        public void WaitOutcome_StillPending_WarnsTimedOut()
        {
            RewindInvokeContext.Pending = true;

            ParsekFlight.LogRewindInvokeContextWaitOutcome(300, 300);

            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: timed out waiting for RewindInvokeContext "
                    + "to clear after 300 frame(s); proceeding without marker swap"));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: waited"));
        }

        [Fact]
        public void WaitOutcome_Cleared_LogsFramesWaited()
        {
            RewindInvokeContext.Pending = false;

            ParsekFlight.LogRewindInvokeContextWaitOutcome(7, 300);

            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: waited 7 frame(s) for RewindInvokeContext to clear"));
            Assert.Equal(0, Count("timed out waiting"));
        }

        // ---- ApplyInPlaceContinuationMarkerSwap -----------------------------------------

        [Fact]
        public void MarkerSwap_NoMarker_KeepsIdAndLogsNothing()
        {
            var tree = Tree("a", Rec("a", "A", 1), Rec("b", "B", 2));
            logLines.Clear();

            string id = ParsekFlight.ApplyInPlaceContinuationMarkerSwap(tree, "a");

            Assert.Equal("a", id);
            Assert.Equal("a", tree.ActiveRecordingId);
            Assert.Equal(0, Count("RestoreActiveTreeFromPending:"));
        }

        [Fact]
        public void MarkerSwap_InPlaceMarkerOnThisTree_SwapsTargetAndRebuildsBackgroundMap()
        {
            var origin = Rec("a", "Origin", 100);
            var fork = Rec("b", "Fork", 200);
            var tree = Tree("a", origin, fork);
            tree.RebuildBackgroundMap();
            Assert.True(tree.BackgroundMap.ContainsKey(200u));
            int bgBefore = tree.BackgroundMap.Count;
            InstallMarker(InPlaceMarker("b", "a"));
            logLines.Clear();

            string id = ParsekFlight.ApplyInPlaceContinuationMarkerSwap(tree, "a");

            Assert.Equal("b", id);
            Assert.Equal("b", tree.ActiveRecordingId);
            Assert.False(tree.BackgroundMap.ContainsKey(200u));
            Assert.Contains(logLines, l => l.Contains("[INFO][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: in-place continuation marker swapped target "
                    + "rec='a'->'b' vessel='Origin'->'Fork' pid=100->200 sess=sess-restore "
                    + "bgMapEntries=" + bgBefore + "->" + tree.BackgroundMap.Count));
        }

        [Fact]
        public void MarkerSwap_MarkerOnAnotherTree_LogsNoSwapReason()
        {
            var tree = Tree("a", Rec("a", "A", 1), Rec("b", "B", 2));
            InstallMarker(InPlaceMarker("b", "a", treeId: "another-tree"));
            logLines.Clear();

            string id = ParsekFlight.ApplyInPlaceContinuationMarkerSwap(tree, "a");

            Assert.Equal("a", id);
            Assert.Equal("a", tree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: marker present but no swap "
                    + "(reason=marker-tree-id-mismatch sess=sess-restore)"));
        }

        [Fact]
        public void MarkerSwap_ForkOnlyInCommittedList_ReconcilesThenSwaps()
        {
            RecordingStore.SuppressLogging = true;
            var origin = Rec("a", "Origin", 100);
            origin.MergeState = MergeState.Immutable;
            var tree = Tree("a", origin);
            var fork = Rec("b", "Origin", 100);
            fork.MergeState = MergeState.NotCommitted;
            fork.SupersedeTargetId = "a";
            RecordingStore.AddCommittedInternal(fork);
            InstallMarker(InPlaceMarker("b", "a"));
            logLines.Clear();

            string id = ParsekFlight.ApplyInPlaceContinuationMarkerSwap(tree, "a");

            Assert.Equal("b", id);
            Assert.Same(fork, tree.Recordings["b"]);
            Assert.Equal("b", tree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("RestoreActiveTreeFromPending:reconcile")
                && l.Contains("attached in-place fork rec=b"));
            Assert.Equal(1, Count("in-place continuation marker swapped target rec='a'->'b'"));
        }

        // ---- TryResolvePendingRestoreActiveRecording ------------------------------------

        [Fact]
        public void Resolve_EmptyId_WarnsAndFails()
        {
            var tree = Tree(null, Rec("a", "A", 1));

            Assert.False(ParsekFlight.TryResolvePendingRestoreActiveRecording(tree, null, out Recording rec));
            Assert.Null(rec);
            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: pending tree 'Restore Tree' has no resolvable "
                    + "activeRecordingId — leaving in Limbo"));
        }

        [Fact]
        public void Resolve_IdNotInTree_WarnsAndFails()
        {
            var tree = Tree("missing", Rec("a", "A", 1));

            Assert.False(ParsekFlight.TryResolvePendingRestoreActiveRecording(tree, "missing", out Recording rec));
            Assert.Null(rec);
            Assert.Equal(1, Count("has no resolvable activeRecordingId"));
        }

        [Fact]
        public void Resolve_IdInTree_ReturnsTheRecording()
        {
            var a = Rec("a", "A", 1);
            var tree = Tree("a", a);

            Assert.True(ParsekFlight.TryResolvePendingRestoreActiveRecording(tree, "a", out Recording rec));
            Assert.Same(a, rec);
            Assert.Equal(0, Count("has no resolvable activeRecordingId"));
        }

        // ---- MatchPendingRestoreTarget --------------------------------------------------

        private sealed class MatchRun
        {
            internal RecordingTree Tree;
            internal Recording Active;
            internal bool LoggedGuidReject;
            internal HashSet<string> ParentRejects = new HashSet<string>();
            internal Recording MatchedRec;
            internal string MatchedRecId;

            internal MatchRun(RecordingTree tree, Recording active)
            {
                Tree = tree;
                Active = active;
                MatchedRec = active;
                MatchedRecId = active.RecordingId;
            }

            internal ParsekFlight.PendingRestoreTargetMatch Frame(uint livePid, string liveName, Guid liveId)
            {
                return ParsekFlight.MatchPendingRestoreTarget(
                    Tree, Active, Active.RecordingId, Active.VesselName, Active.VesselPersistentId,
                    livePid, liveName, liveId,
                    ref LoggedGuidReject, ParentRejects, ref MatchedRec, ref MatchedRecId);
            }
        }

        private static readonly Guid LiveId = new Guid("0123456789abcdef0123456789abcdef");
        private const string LiveGuidN = "0123456789abcdef0123456789abcdef";
        private const string OtherGuidN = "fedcba9876543210fedcba9876543210";

        [Fact]
        public void Match_FreshRollout_IsRefusedBeforeAnyMatch()
        {
            var a = Rec("a", "Ship", 500);
            var run = new MatchRun(Tree("a", a), a);
            SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = 500;

            var result = run.Frame(500, "Ship", LiveId);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.RefusedFreshRollout, result);
            Assert.Same(a, run.MatchedRec);
            Assert.Contains(logLines, l => l.Contains("[INFO][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: refusing to adopt fresh-rollout vessel "
                    + "'Ship' pid=500 liveGuid=" + LiveGuidN + " for recording 'a' (recordedPid=500, "
                    + "recordedGuid=<none>)"));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: PID match"));
        }

        [Fact]
        public void Match_PidWithUnknownGuid_Matches()
        {
            var a = Rec("a", "Ship", 500);
            var run = new MatchRun(Tree("a", a), a);

            var result = run.Frame(500, "Other Name", LiveId);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, result);
            Assert.Same(a, run.MatchedRec);
            Assert.Equal("a", run.MatchedRecId);
            Assert.Equal(1, Count("RestoreActiveTreeFromPending: PID match pid=500"));
        }

        [Fact]
        public void Match_PidWithAgreeingGuid_Matches()
        {
            var a = Rec("a", "Ship", 500, guid: LiveGuidN);
            var run = new MatchRun(Tree("a", a), a);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, run.Frame(500, "Ship", LiveId));
            Assert.Equal(0, Count("conclusively differs"));
        }

        [Fact]
        public void Match_PidWithDifferingGuid_IsRejectedAndLoggedOncePerRun()
        {
            var a = Rec("a", "Ship", 500, guid: OtherGuidN);
            var run = new MatchRun(Tree("a", a), a);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(500, "Ship", LiveId));
            Assert.True(run.LoggedGuidReject);
            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(500, "Ship", LiveId));

            Assert.Equal(1, Count(
                "RestoreActiveTreeFromPending: active vessel 'Ship' pid=500 liveGuid=" + LiveGuidN
                + " conclusively differs from recording 'a' recordedGuid=" + OtherGuidN));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: PID match"));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: name match"));
        }

        [Fact]
        public void Match_EmptyLiveGuid_NeverRejectsOnGuid()
        {
            var a = Rec("a", "Ship", 500, guid: OtherGuidN);
            var run = new MatchRun(Tree("a", a), a);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, run.Frame(500, "Ship", Guid.Empty));
            Assert.False(run.LoggedGuidReject);
        }

        [Fact]
        public void Match_IdentityLessRecording_MatchesByName()
        {
            var a = Rec("a", "Ship", 0);
            var run = new MatchRun(Tree("a", a), a);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(77, "Other", LiveId));
            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, run.Frame(77, "Ship", LiveId));
            Assert.Equal(1, Count("RestoreActiveTreeFromPending: name match 'Ship'"));
        }

        [Fact]
        public void Match_RecordingWithPid_NeverMatchesByName()
        {
            var a = Rec("a", "Ship", 500);
            var run = new MatchRun(Tree("a", a), a);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(77, "Ship", LiveId));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: name match"));
        }

        [Fact]
        public void Match_RecordingWithGuidOnly_NeverMatchesByName()
        {
            var a = Rec("a", "Ship", 0, guid: LiveGuidN);
            var run = new MatchRun(Tree("a", a), a);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(77, "Ship", LiveId));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: name match"));
        }

        [Fact]
        public void Match_EvaParentByPid_MatchesAndFlipsTheTreePointer()
        {
            var parent = Rec("parent", "Ship", 500);
            var eva = Rec("eva", "Jeb Kerman", 900, parentId: "parent");
            var tree = Tree("eva", parent, eva);
            var run = new MatchRun(tree, eva);

            var result = run.Frame(500, "Ship", LiveId);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, result);
            Assert.Same(parent, run.MatchedRec);
            Assert.Equal("parent", run.MatchedRecId);
            Assert.Equal("parent", tree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("[INFO][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: fallback match — active vessel pid=500 "
                    + "matches parent recording 'parent' (original active was 'Jeb Kerman'"));
        }

        [Fact]
        public void Match_EvaParentByName_OnlyWhenTheParentIsIdentityLess()
        {
            var parent = Rec("parent", "Ship", 0);
            var eva = Rec("eva", "Jeb Kerman", 900, parentId: "parent");
            var tree = Tree("eva", parent, eva);
            var run = new MatchRun(tree, eva);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, run.Frame(42, "Ship", LiveId));
            Assert.Equal("parent", run.MatchedRecId);
        }

        [Fact]
        public void Match_GuidRejectedActiveButAgreeingParent_MatchesTheParent()
        {
            var parent = Rec("parent", "Ship", 500, guid: LiveGuidN);
            var eva = Rec("eva", "Jeb Kerman", 500, guid: OtherGuidN, parentId: "parent");
            var tree = Tree("eva", parent, eva);
            var run = new MatchRun(tree, eva);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, run.Frame(500, "Ship", LiveId));
            Assert.Equal("parent", run.MatchedRecId);
            Assert.Equal(1, Count("conclusively differs from recording 'eva'"));
            Assert.Equal(0, Count("RestoreActiveTreeFromPending: PID match"));
        }

        [Fact]
        public void Match_GuidRejectedParent_IsSteppedOverToTheNextAncestor()
        {
            var root = Rec("root-recording", "Ship", 500);
            var mid = Rec("mid-recording", "Ship", 500, guid: OtherGuidN, parentId: "root-recording");
            var eva = Rec("eva", "Jeb Kerman", 900, parentId: "mid-recording");
            var tree = Tree("eva", root, mid, eva);
            var run = new MatchRun(tree, eva);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, run.Frame(500, "Ship", LiveId));
            Assert.Equal("root-recording", run.MatchedRecId);
            Assert.Equal("root-recording", tree.ActiveRecordingId);
            Assert.Equal(1, Count("RestoreActiveTreeFromPending: parent-walk guid gate rejected rec=mid-reco "));
        }

        [Fact]
        public void Match_GuidRejectedParent_IsLoggedOnceAcrossFrames()
        {
            var mid = Rec("mid-recording", "Ship", 500, guid: OtherGuidN);
            var eva = Rec("eva", "Jeb Kerman", 900, parentId: "mid-recording");
            var tree = Tree("eva", mid, eva);
            var run = new MatchRun(tree, eva);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(500, "Ship", LiveId));
            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(500, "Ship", LiveId));
            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(500, "Ship", LiveId));

            Assert.Contains("mid-recording", run.ParentRejects);
            Assert.Equal(1, Count("RestoreActiveTreeFromPending: parent-walk guid gate rejected rec=mid-reco "));
            Assert.Equal("eva", tree.ActiveRecordingId);
        }

        [Fact]
        public void Match_ParentWalk_StopsAfterFiveAncestors()
        {
            // eva -> p1 -> p2 -> p3 -> p4 -> p5 -> p6; only one ancestor carries the live pid.
            Recording[] Chain(int matchingDepth)
            {
                var recs = new List<Recording>();
                for (int d = 6; d >= 1; d--)
                    recs.Add(Rec("p" + d, "P" + d, d == matchingDepth ? 500u : 600u + (uint)d,
                        parentId: d < 6 ? "p" + (d + 1) : null));
                recs.Add(Rec("eva", "Jeb Kerman", 900, parentId: "p1"));
                return recs.ToArray();
            }

            var five = Chain(5);
            var runFive = new MatchRun(Tree("eva", five), five.Last());
            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.Matched, runFive.Frame(500, "X", LiveId));
            Assert.Equal("p5", runFive.MatchedRecId);

            var six = Chain(6);
            var runSix = new MatchRun(Tree("eva", six), six.Last());
            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, runSix.Frame(500, "X", LiveId));
            Assert.Equal("eva", runSix.MatchedRecId);
        }

        [Fact]
        public void Match_ParentMissingFromTree_EndsTheWalk()
        {
            var eva = Rec("eva", "Jeb Kerman", 900, parentId: "gone");
            var tree = Tree("eva", eva);
            var run = new MatchRun(tree, eva);

            Assert.Equal(ParsekFlight.PendingRestoreTargetMatch.None, run.Frame(500, "Ship", LiveId));
            Assert.Equal("eva", tree.ActiveRecordingId);
        }

        // ---- ReportPendingRestoreGiveUp -------------------------------------------------

        [Fact]
        public void GiveUp_WithoutReFlySession_WarnsLimboOnly()
        {
            var tree = Tree("a", Rec("a", "Ship", 500));

            ParsekFlight.ReportPendingRestoreGiveUp(tree, "a", "Ship", 500);

            Assert.Contains(logLines, l => l.Contains("[WARN][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: vessel 'Ship' (and no EVA parent fallback) "
                    + "not active within 3s — leaving tree in Limbo"));
            Assert.Equal(0, Count("outcome=unbound-refly-provisional"));
            Assert.Empty(screenMessages);
        }

        [Fact]
        public void GiveUp_UnderInPlaceReFly_RaisesTheUnboundProvisional()
        {
            var tree = Tree("a", Rec("a", "Ship", 500));
            InstallMarker(InPlaceMarker("fork", "origin"));

            ParsekFlight.ReportPendingRestoreGiveUp(tree, "a", "Ship", 500);

            Assert.Equal(1, Count("not active within 3s"));
            Assert.Contains(logLines, l => l.Contains("[WARN][ReFlySession]")
                && l.Contains("outcome=unbound-refly-provisional reason=refly-restore-gave-up-on-marker-tree "
                    + "sess=sess-restore provisional=fork markerTree=tree-restore attemptedTree=tree-restore "
                    + "attemptedRec=a attemptedVessel='Ship' attemptedPid=500"));
            Assert.Single(screenMessages);
            Assert.Contains("re-fly attempt is not recording", screenMessages[0]);
        }

        [Fact]
        public void GiveUp_UnderInPlaceReFlyOnAnotherTree_NamesTheOtherTree()
        {
            var tree = Tree("a", Rec("a", "Ship", 500));
            InstallMarker(InPlaceMarker("fork", "origin", treeId: "marker-tree"));

            ParsekFlight.ReportPendingRestoreGiveUp(tree, "a", "Ship", 500);

            Assert.Equal(1, Count("reason=refly-restore-gave-up-on-other-tree"));
        }

        // ---- RemapRestoredRecordingPid --------------------------------------------------

        [Fact]
        public void Remap_SamePid_ChangesNothing()
        {
            var a = Rec("a", "Ship", 500);
            var tree = Tree("a", a);
            tree.BackgroundMap[500u] = "other";

            ParsekFlight.RemapRestoredRecordingPid(tree, a, "a", 500, 500, "Ship");

            Assert.Equal(500u, a.VesselPersistentId);
            Assert.Equal("other", tree.BackgroundMap[500u]);
            Assert.Equal(0, Count("PID remap"));
        }

        [Fact]
        public void Remap_NewPid_MovesAnotherRecordingsBackgroundEntry()
        {
            var a = Rec("a", "Ship", 500);
            var tree = Tree("a", a);
            tree.BackgroundMap.Clear();
            tree.BackgroundMap[500u] = "b";

            ParsekFlight.RemapRestoredRecordingPid(tree, a, "a", 500, 501, "Ship");

            Assert.Equal(501u, a.VesselPersistentId);
            Assert.False(tree.BackgroundMap.ContainsKey(500u));
            Assert.Equal("b", tree.BackgroundMap[501u]);
            Assert.Contains(logLines, l => l.Contains("[INFO][Flight]")
                && l.Contains("RestoreActiveTreeFromPending: PID remap 500 → 501 for 'Ship'"));
        }

        [Fact]
        public void Remap_NewPid_DropsTheActiveRecordingsOwnBackgroundEntry()
        {
            var a = Rec("a", "Ship", 500);
            var tree = Tree("a", a);
            tree.BackgroundMap.Clear();
            tree.BackgroundMap[500u] = "a";

            ParsekFlight.RemapRestoredRecordingPid(tree, a, "a", 500, 501, "Ship");

            Assert.Empty(tree.BackgroundMap);
            Assert.Equal(501u, a.VesselPersistentId);
        }

        [Fact]
        public void Remap_FromPidZero_SetsThePidWithoutTouchingTheMap()
        {
            var a = Rec("a", "Ship", 0);
            var tree = Tree("a", a);
            tree.BackgroundMap.Clear();
            tree.BackgroundMap[0u] = "b";

            ParsekFlight.RemapRestoredRecordingPid(tree, a, "a", 0, 501, "Ship");

            Assert.Equal(501u, a.VesselPersistentId);
            Assert.Equal("b", tree.BackgroundMap[0u]);
            Assert.False(tree.BackgroundMap.ContainsKey(501u));
            Assert.Equal(1, Count("PID remap 0 → 501 for 'Ship'"));
        }

        // ---- ApplyPendingActiveTreeResumeRewindSave -------------------------------------

        [Fact]
        public void RewindSave_Pending_IsHandedToTheRecorderAndConsumed()
        {
            var recorder = new FlightRecorder();
            ParsekScenario.pendingActiveTreeResumeRewindSave = "parsek_rw_abc";

            ParsekFlight.ApplyPendingActiveTreeResumeRewindSave(recorder);

            Assert.Equal("parsek_rw_abc", recorder.RewindSaveFileName);
            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.Contains(logLines, l => l.Contains("RewindSaveFileName set for restore")
                && l.Contains("quickload-resume from PARSEK_ACTIVE_TREE"));
        }

        [Fact]
        public void RewindSave_None_LeavesTheRecorderAlone()
        {
            var recorder = new FlightRecorder();
            ParsekScenario.pendingActiveTreeResumeRewindSave = "";

            ParsekFlight.ApplyPendingActiveTreeResumeRewindSave(recorder);

            Assert.Null(recorder.RewindSaveFileName);
            Assert.Equal("", ParsekScenario.pendingActiveTreeResumeRewindSave);
            Assert.Equal(0, Count("RewindSaveFileName set for restore"));
        }
    }
}
