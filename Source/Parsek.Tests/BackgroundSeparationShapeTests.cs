using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q15 (2026-11-18, "one shape for both"): a decouple or a broken-off part of a
    /// BACKGROUND vessel gives the tree shape a focused one gives. The parent recording continues
    /// (no close, no parent continuation), and the pieces that come off inside one coalescing
    /// window are children of ONE JointBreak branch point anchored to the parent recording.
    /// The live vessel scan cannot run headless, so these cells feed the window core directly
    /// with the children already resolved (no live vessel: a debris piece becomes the focused
    /// shape's destroyed-in-window row, a controlled piece is skipped as dead on arrival).
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundSeparationShapeTests : IDisposable
    {
        private const uint ParentPid = 7100u;
        private const string ParentRecId = "rec_bg_parent";
        private readonly List<string> logLines = new List<string>();

        public BackgroundSeparationShapeTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static RecordingTree MakeTree(out Recording parent)
        {
            var tree = new RecordingTree
            {
                Id = "tree_q15",
                TreeName = "Q15 Tree",
                RootRecordingId = "rec_active",
                ActiveRecordingId = "rec_active"
            };
            tree.Recordings["rec_active"] = new Recording
            {
                RecordingId = "rec_active",
                TreeId = tree.Id,
                VesselName = "Active Vessel",
                VesselPersistentId = 1u,
                ExplicitStartUT = 100.0
            };
            parent = new Recording
            {
                RecordingId = ParentRecId,
                TreeId = tree.Id,
                VesselName = "Background Stack",
                VesselPersistentId = ParentPid,
                ExplicitStartUT = 100.0,
                // What OnBackgroundPartJointBreak leaves: its structural point at the
                // joint-break UT (the branch UT) is the parent's latest sample, so its end
                // equals the branch UT until the next ordinary sample, seconds later.
                ExplicitEndUT = 150.0,
                Generation = 0
            };
            tree.Recordings[ParentRecId] = parent;
            tree.BackgroundMap[ParentPid] = ParentRecId;
            return tree;
        }

        private static BackgroundRecorder MakeRecorder(RecordingTree tree)
        {
            var bg = new BackgroundRecorder(tree);
            bg.InjectLoadedStateForTesting(ParentPid, ParentRecId);
            return bg;
        }

        private static ConfigNode Snapshot(string name, uint pid)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", name);
            node.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return node;
        }

        private static TrajectoryPoint Seed(double ut)
            => new TrajectoryPoint { ut = ut, altitude = 900.0, bodyName = "Kerbin" };

        private static int OpenWindowCount(BackgroundRecorder bg)
        {
            var field = typeof(BackgroundRecorder).GetField(
                "separationWindows", BindingFlags.Instance | BindingFlags.NonPublic);
            return ((ICollection)field.GetValue(bg)).Count;
        }

        [Theory]
        [InlineData("DECOUPLE", true)]
        [InlineData("BROKE_OFF", true)]
        [InlineData(null, true)]
        [InlineData("UNDOCK", false)]
        public void IsCoalescedBackgroundSeparationCause_OnlyUndockKeepsTheClosingShape(
            string cause, bool expected)
        {
            Assert.Equal(expected, BackgroundRecorder.IsCoalescedBackgroundSeparationCause(cause));
        }

        [Fact]
        public void Decouple_ParentContinues_OneBranchPoint_NoContinuation()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);

            Assert.True(bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false,
                Snapshot("Stage A", 7201u), Seed(150.02)));
            Assert.False(bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7202u, false,
                Snapshot("Stage B", 7202u), Seed(150.02)));
            Assert.Equal(1, OpenWindowCount(bg));
            Assert.Empty(tree.BranchPoints);

            Assert.True(bg.EmitBackgroundSeparationWindowNow(ParentPid, "test"));

            Assert.Equal(0, OpenWindowCount(bg));
            BranchPoint bp = Assert.Single(tree.BranchPoints);
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal("DECOUPLE", bp.SplitCause);
            Assert.Equal(150.0, bp.UT);
            Assert.Equal(new List<string> { ParentRecId }, bp.ParentRecordingIds);
            Assert.Equal(2, bp.ChildRecordingIds.Count);

            // The parent continues: it keeps its own BackgroundMap entry, carries the branch
            // point and is still where its vessel's line ends (one recordings-table row).
            Assert.Equal(bp.Id, parent.ChildBranchPointId);
            Assert.Equal(ParentRecId, tree.BackgroundMap[ParentPid]);
            Assert.True(tree.IsVesselLineEnd(parent));
            Assert.False(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parent, tree));
            Assert.Equal(150.0, parent.ExplicitEndUT);
            Assert.True(bp.ParentContinues);

            // No parent continuation: no recording other than the parent carries its pid.
            int parentPidRecordings = 0;
            foreach (var rec in tree.Recordings.Values)
                if (rec.VesselPersistentId == ParentPid) parentPidRecordings++;
            Assert.Equal(1, parentPidRecordings);
            Assert.Equal(4, tree.Recordings.Count); // active + parent + two pieces

            foreach (string childId in bp.ChildRecordingIds)
            {
                Recording child = tree.Recordings[childId];
                Assert.True(child.IsDebris);
                Assert.Equal(ParentRecId, child.ParentAnchorRecordingId);
                Assert.Equal(bp.Id, child.ParentBranchPointId);
                Assert.Equal(150.0, child.ExplicitStartUT);
                Assert.Equal(1, child.Generation);
                Assert.NotNull(child.GhostVisualSnapshot);
            }

            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Background separation fed to window") && l.Contains("childPid=7201")
                && l.Contains("opened=true"));
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Separation window emitted early (test)") && l.Contains("parentPid=7100"));
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Background separation branch complete")
                && l.Contains("parentContinues=true") && l.Contains("children=2")
                && l.Contains("debris=2"));
        }

        [Fact]
        public void SplitsInsideTheWindow_CoalesceIntoOneBranchPoint_BrokeOffWins()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);

            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false,
                Snapshot("Stage A", 7201u), Seed(150.02));
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.3, SegmentBoundaryLogic.BrokeOffSplitCause, 7202u, false,
                Snapshot("Fin", 7202u), Seed(150.32));

            Assert.Equal(0, bg.TickBackgroundSeparationWindows(150.4));
            Assert.Empty(tree.BranchPoints);
            Assert.Equal(1, bg.TickBackgroundSeparationWindows(150.5));

            BranchPoint bp = Assert.Single(tree.BranchPoints);
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal(SegmentBoundaryLogic.BrokeOffSplitCause, bp.SplitCause);
            Assert.Equal(150.0, bp.UT);
            Assert.Equal(2, bp.ChildRecordingIds.Count);
            Assert.Equal(bp.Id, parent.ChildBranchPointId);
            Assert.Contains(logLines, l => l.Contains("reason=window-expired"));
        }

        [Fact]
        public void NullCause_IsABrokeOffSeparation()
        {
            var tree = MakeTree(out _);
            var bg = MakeRecorder(tree);

            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, null, 7201u, false, Snapshot("Piece", 7201u), Seed(150.02));
            bg.EmitBackgroundSeparationWindowNow(ParentPid, "test");

            Assert.Equal(SegmentBoundaryLogic.BrokeOffSplitCause, Assert.Single(tree.BranchPoints).SplitCause);
        }

        [Fact]
        public void ControlledChildGoneBeforeTheWindowCloses_GetsNoRecording()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);

            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7301u, true,
                Snapshot("Probe", 7301u), Seed(150.02));
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7302u, false,
                Snapshot("Stage", 7302u), Seed(150.02));
            bg.EmitBackgroundSeparationWindowNow(ParentPid, "test");

            BranchPoint bp = Assert.Single(tree.BranchPoints);
            string childId = Assert.Single(bp.ChildRecordingIds);
            Assert.Equal(7302u, tree.Recordings[childId].VesselPersistentId);
            Assert.Equal(bp.Id, parent.ChildBranchPointId);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("controlled child gone before the window closed") && l.Contains("pid=7301"));
        }

        [Fact]
        public void Flush_EmitsEveryOpenWindow_OntoItsOwnParent()
        {
            var tree = MakeTree(out Recording parent);
            var other = new Recording
            {
                RecordingId = "rec_bg_other",
                TreeId = tree.Id,
                VesselName = "Other Stack",
                VesselPersistentId = 7400u,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 151.0
            };
            tree.Recordings[other.RecordingId] = other;
            tree.BackgroundMap[7400u] = other.RecordingId;
            var bg = MakeRecorder(tree);

            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false, Snapshot("A", 7201u), Seed(150.02));
            bg.FeedBackgroundSeparationWindowCore(
                7400u, other.RecordingId, 151.0, "DECOUPLE", 7401u, false, Snapshot("B", 7401u), Seed(151.02));

            Assert.Equal(2, bg.FlushBackgroundSeparationWindows("OnSave"));
            Assert.Equal(0, OpenWindowCount(bg));
            Assert.Equal(2, tree.BranchPoints.Count);
            Assert.NotNull(parent.ChildBranchPointId);
            Assert.NotNull(other.ChildBranchPointId);
            Assert.NotEqual(parent.ChildBranchPointId, other.ChildBranchPointId);
            Assert.Contains(logLines, l => l.Contains("Background separation windows flushed (OnSave): emitted=2"));

            // Nothing open: a second flush emits nothing and logs nothing.
            logLines.Clear();
            Assert.Equal(0, bg.FlushBackgroundSeparationWindows("OnSave"));
            Assert.DoesNotContain(logLines, l => l.Contains("Background separation windows flushed"));
        }

        [Fact]
        public void Shutdown_EmitsAnOpenWindowInsteadOfDroppingIt()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false, Snapshot("A", 7201u), Seed(150.02));

            bg.Shutdown(150.2);

            Assert.Single(tree.BranchPoints);
            Assert.Equal(tree.BranchPoints[0].Id, parent.ChildBranchPointId);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[BgRecorder]")
                && l.Contains("Separation window(s) still open at shutdown: count=1"));
        }

        [Fact]
        public void RetireDestroyedEntry_EmitsTheWindowFirst()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false, Snapshot("A", 7201u), Seed(150.02));

            bg.RetireDestroyedBackgroundEntry(ParentPid, ParentRecId, 150.3);

            Assert.Single(tree.BranchPoints);
            Assert.Equal(tree.BranchPoints[0].Id, parent.ChildBranchPointId);
            Assert.False(tree.BackgroundMap.ContainsKey(ParentPid));
            Assert.Contains(logLines, l => l.Contains("Separation window emitted early (retire-destroyed)"));
        }

        [Fact]
        public void WindowOfAnotherRecordingOfTheVessel_IsEmittedBeforeANewOneOpens()
        {
            var tree = MakeTree(out Recording parent);
            var next = new Recording
            {
                RecordingId = "rec_bg_parent_next",
                TreeId = tree.Id,
                VesselName = "Background Stack",
                VesselPersistentId = ParentPid,
                ExplicitStartUT = 150.1
            };
            tree.Recordings[next.RecordingId] = next;
            var bg = MakeRecorder(tree);

            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false, Snapshot("A", 7201u), Seed(150.02));
            Assert.True(bg.FeedBackgroundSeparationWindowCore(
                ParentPid, next.RecordingId, 150.2, "DECOUPLE", 7202u, false, Snapshot("B", 7202u), Seed(150.22)));

            BranchPoint first = Assert.Single(tree.BranchPoints);
            Assert.Equal(first.Id, parent.ChildBranchPointId);
            Assert.Equal(1, OpenWindowCount(bg));
            Assert.Contains(logLines, l => l.Contains("Separation window belongs to another recording of the vessel"));
        }

        [Fact]
        public void ParentRecordingMissing_SeparationIsNotWired_AndLogged()
        {
            var tree = MakeTree(out _);
            var bg = MakeRecorder(tree);
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, "rec_gone", 150.0, "DECOUPLE", 7201u, false, Snapshot("A", 7201u), Seed(150.02));

            bg.EmitBackgroundSeparationWindowNow(ParentPid, "test");

            Assert.Empty(tree.BranchPoints);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("Background separation not recorded: parent recording missing")
                && l.Contains("parentRec=rec_gone"));
        }

        [Fact]
        public void ControlledPieceSeparatingAgain_EmitsItsWindowFirst_DebrisPieceDoesNot()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7301u, true, Snapshot("Probe", 7301u), Seed(150.02));
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7302u, false, Snapshot("Stage", 7302u), Seed(150.02));

            // A debris piece is not looked up (plain debris records only its own separation).
            Assert.False(bg.EmitSeparationWindowHoldingControlledChild(7302u, "child-separated-again"));
            Assert.Equal(1, OpenWindowCount(bg));

            Assert.True(bg.EmitSeparationWindowHoldingControlledChild(7301u, "child-separated-again"));
            Assert.Equal(0, OpenWindowCount(bg));
            Assert.Single(tree.BranchPoints);
            Assert.Equal(tree.BranchPoints[0].Id, parent.ChildBranchPointId);
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Controlled piece separates again inside its window: childPid=7301 parentPid=7100"));
            Assert.Contains(logLines, l => l.Contains("Separation window emitted early (child-separated-again)"));

            // Nothing open any more: an unknown piece finds no window.
            Assert.False(bg.EmitSeparationWindowHoldingControlledChild(7301u, "child-separated-again"));
        }

        [Fact]
        public void JointBreakHandler_EmitsTheHoldingWindowBeforeItsMembershipCheck()
        {
            string body = MethodBody(
                ReadSource(Path.Combine("Recording", "BackgroundRecorder.GameEventHandlers.cs")),
                "internal void OnBackgroundPartJointBreak(");
            int emit = body.IndexOf("EmitSeparationWindowHoldingControlledChild(vesselPid,", StringComparison.Ordinal);
            int membership = body.IndexOf("if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;", StringComparison.Ordinal);
            Assert.True(emit > 0 && membership > emit, $"emit={emit} membership={membership}");
        }

        // B1: the parent's end equals the branch UT right after the split (the joint break's
        // structural point is its latest sample). The stamped branch point keeps it on its
        // vessel's line, so a save then load rebuilds it into the BackgroundMap.
        [Fact]
        public void ParentEndingAtTheBranchUT_StaysTheVesselLine_AndSurvivesTheMapRebuild()
        {
            var tree = MakeTree(out Recording parent);
            var bg = MakeRecorder(tree);
            bg.FeedBackgroundSeparationWindowCore(
                ParentPid, ParentRecId, 150.0, "DECOUPLE", 7201u, false, Snapshot("A", 7201u), Seed(150.02));
            bg.FlushBackgroundSeparationWindows("OnSave");

            Assert.Equal(150.0, parent.ExplicitEndUT);
            Assert.True(tree.IsVesselLineEnd(parent));

            tree.RebuildBackgroundMap();
            Assert.True(tree.BackgroundMap.TryGetValue(ParentPid, out string mapped));
            Assert.Equal(ParentRecId, mapped);

            // The same state through a save / load of the branch point.
            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, tree.BranchPoints[0]);
            Assert.Equal("True", node.GetValue("parentContinues"));
            tree.BranchPoints[0] = RecordingTree.LoadBranchPointFrom(node);
            Assert.True(tree.BranchPoints[0].ParentContinues);
            Assert.True(tree.IsVesselLineEnd(parent));
        }

        [Fact]
        public void UnstampedBranchPoint_KeepsTheClosedAtSplitReading()
        {
            // An older save (or the background undock's close): no parentContinues key, the
            // parent's end at the branch UT and no same-pid child still reads as closed there.
            var tree = MakeTree(out Recording parent);
            var bp = new BranchPoint
            {
                Id = "bp_old",
                UT = 150.0,
                Type = BranchPointType.JointBreak,
                SplitCause = "DECOUPLE",
                ParentRecordingIds = new List<string> { ParentRecId }
            };
            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Null(node.GetValue("parentContinues"));
            BranchPoint loaded = RecordingTree.LoadBranchPointFrom(node);
            Assert.False(loaded.ParentContinues);
            tree.BranchPoints.Add(loaded);
            parent.ChildBranchPointId = loaded.Id;

            Assert.False(tree.IsVesselLineEnd(parent));
        }

        [Fact]
        public void WireBreakupIntoTree_StampsParentContinues_ForTheFocusedPathToo()
        {
            var tree = MakeTree(out Recording parent);
            var bp = new BranchPoint { Id = "bp_focused", UT = 150.0, Type = BranchPointType.Breakup };

            ParsekFlight.WireBreakupIntoTree(tree, parent.RecordingId, parent, bp);

            Assert.True(bp.ParentContinues);
            Assert.Equal(bp.Id, parent.ChildBranchPointId);
        }

        [Fact]
        public void ScenarioWriterSeparationBranch_AuthorsTheStamp()
        {
            Assert.True(ScenarioWriter.SeparationBranch(
                "bp", "parent", new[] { "child" }, 150.0, parentContinues: true).ParentContinues);
            Assert.False(ScenarioWriter.SeparationBranch(
                "bp", "parent", new[] { "child" }, 150.0).ParentContinues);
        }

        [Fact]
        public void NoOpenWindow_EmitNowAndTickAreNoOps()
        {
            var tree = MakeTree(out _);
            var bg = MakeRecorder(tree);

            Assert.False(bg.EmitBackgroundSeparationWindowNow(ParentPid, "test"));
            Assert.Equal(0, bg.TickBackgroundSeparationWindows(500.0));
            Assert.Empty(tree.BranchPoints);
        }

        // ---- Source gates: where the windows are emitted (never drop one) ----

        private static string ReadSource(string relativePath)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return File.ReadAllText(Path.Combine(root, "Source", "Parsek", relativePath));
        }

        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "signature not found: " + signature);
            int brace = source.IndexOf('{', start);
            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(brace, i - brace + 1);
            }
            throw new InvalidOperationException("unbalanced body: " + signature);
        }

        [Fact]
        public void FocusedFlushPendingSplits_AlsoFlushesTheBackgroundWindows()
        {
            string body = MethodBody(
                ReadSource(Path.Combine("Controllers", "ParsekFlightSplitBrancher.cs")),
                "internal bool FlushPendingSplits(");
            Assert.Contains("backgroundRecorder?.FlushBackgroundSeparationWindows(reason)", body);
        }

        [Theory]
        [InlineData("BackgroundRecorder.cs", "private void OnVesselRemovedFromBackground(uint vesselPid, double? closeUT)", "EmitBackgroundSeparationWindowNow(vesselPid,")]
        [InlineData("BackgroundRecorder.cs", "public void OnBackgroundVesselGoOnRails(Vessel v)", "EmitBackgroundSeparationWindowNow(pid,")]
        [InlineData("BackgroundRecorder.cs", "public void OnBackgroundVesselWillDestroy(Vessel v)", "EmitBackgroundSeparationWindowNow(pid,")]
        [InlineData("BackgroundRecorder.cs", "internal void RetireDestroyedBackgroundEntry(", "EmitBackgroundSeparationWindowNow(pid,")]
        [InlineData("BackgroundRecorder.cs", "internal void Shutdown(double ut)", "FlushBackgroundSeparationWindows(")]
        [InlineData("BackgroundRecorder.Finalization.cs", "public void FinalizeAllForCommit(double commitUT)", "FlushBackgroundSeparationWindows(")]
        [InlineData("BackgroundRecorder.SplitDetection.cs", "internal void ProcessPendingSplitChecks()", "TickBackgroundSeparationWindows(")]
        public void BackgroundRecorder_EmitsTheWindowWhereTheParentStopsBeingBackground(
            string file, string signature, string call)
        {
            string body = MethodBody(ReadSource(Path.Combine("Recording", file)), signature);
            Assert.Contains(call, body);
        }

        [Fact]
        public void HandleBackgroundVesselSplit_FeedsTheWindowBeforeTheClosingPath()
        {
            string body = MethodBody(
                ReadSource(Path.Combine("Recording", "BackgroundRecorder.SplitDetection.cs")),
                "internal void HandleBackgroundVesselSplit(");
            int feed = body.IndexOf("FeedBackgroundSeparationWindow(", StringComparison.Ordinal);
            int undockFlush = body.IndexOf("EmitBackgroundSeparationWindowNow(parentPid, \"undock-closes-parent\")", StringComparison.Ordinal);
            int close = body.IndexOf("CloseParentRecording(", StringComparison.Ordinal);
            int continuation = body.IndexOf("BuildBackgroundSplitParentContinuation(", StringComparison.Ordinal);
            Assert.True(feed > 0 && undockFlush > feed && close > undockFlush && continuation > close,
                $"feed={feed} undockFlush={undockFlush} close={close} continuation={continuation}");
        }
    }
}
