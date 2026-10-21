using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// An in-session load of a quicksave whose flight was committed (before or after the
    /// quicksave) when an OnSave ran in between: the OnSave advanced the sidecar epochs past the
    /// ones the quicksave names, so every member that changed reads `stale-sidecar-epoch` on the
    /// load. A save-file load (F9) is cold since "load restores the save"; these shapes are
    /// reached by a load no stock path announces. Driven through
    /// <c>ParsekScenario.TryRestoreActiveTreeNode</c> and the production resume entry
    /// <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c>.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadCommittedAfterQuicksaveTests : IDisposable
    {
        private const double QuicksaveUT = 200.0;
        private const uint RootPid = 111u;
        private const uint BoosterPid = 222u;

        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public QuickloadCommittedAfterQuicksaveTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            RecordingStore.SuppressLogging = false;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            saveRoot = Path.Combine(Path.GetTempPath(), "parsek-ql-after-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(saveRoot);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            try { Directory.Delete(saveRoot, true); } catch { }
            ResetAll();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RewindContext.ResetForTesting();
            RevertDetector.ResetForTesting();
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
        }

        // ============================================================
        // A flight committed after the quicksave, then an in-session load of that quicksave
        // (a load no stock path announces; F9 is a cold load). Phase 3 deleted the
        // committed-copy rule that resumed such a flight: the stale members drop the saved
        // tree and the committed copy stays committed.
        // ============================================================

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CommittedAfterTheQuicksave_StaleSavedTreeDropped_CommittedCopyStays(bool revertPending)
        {
            var committed = CommitFuture("ql_after");
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_after"), committedTree: null);
            if (revertPending)
                RevertDetector.SetPendingForTesting(RevertKind.Launch);

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.Contains(RecordingStore.CommittedTrees, t => ReferenceEquals(t, committed));
            Assert.Contains(logLines, l => l.Contains("dropped entire tree 'ql_after'"));
            AssertNoLine("removed committed tree 'ql_after'");
            AssertNoLine("Quickload committed-copy restore");
        }

        // ============================================================
        // D2: the quicksave already holds the tree as committed history (a copy-on-write
        // clone of a tree committed BEFORE the quicksave): today's behaviour, logged.
        // ============================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CommittedBeforeTheQuicksave_LaterSaveThenF9_KeepsTodaysBehaviour(bool withPendingClone)
        {
            var committed = CommitFuture("ql_d2");
            RecordingTree clone = withPendingClone ? StashResumedClone(committed) : null;
            // The quicksave was taken while the clone flew: OnSave wrote the committed tree
            // first, then the clone as the active tree.
            var quicksavedActive = MakeQuicksaveTree("ql_d2");
            var quicksavedCommitted = MakeQuicksaveTree("ql_d2");
            ConfigNode node = QuicksaveNode(quicksavedActive, quicksavedCommitted);

            logLines.Clear();
            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);

            Assert.Contains(RecordingStore.CommittedTrees, t => ReferenceEquals(t, committed));
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.TreeId == committed.Id);
            Assert.DoesNotContain(logLines, l => l.Contains("removed committed tree 'ql_d2'"));
            if (withPendingClone)
            {
                Assert.True(restored);
                Assert.Contains(logLines, l => l.Contains("keeping in-memory pending tree"));
                Assert.Same(clone, RecordingStore.PendingTree);
                Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
            }
            else
            {
                Assert.False(restored);
                Assert.Contains(logLines, l => l.Contains("dropped entire tree 'ql_d2'"));
            }
        }

        // ============================================================
        // The plain stale-epoch keep (F5, fly on, autosave, F9; nothing committed): unchanged.
        // ============================================================

        [Fact]
        public void NoCommittedCopy_StaleEpochKeepOfThePendingTree_Unchanged()
        {
            var future = MakeFutureTree("ql_plain");
            WriteSidecarsAtEpoch(future, 2);
            RecordingStore.StashPendingTree(future, PendingTreeState.Limbo);
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_plain"), committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.Same(future, RecordingStore.PendingTree);
            Assert.Contains(logLines, l => l.Contains("keeping in-memory pending tree"));
            Assert.DoesNotContain(logLines, l => l.Contains("removed committed tree"));
        }

        // ============================================================
        // The Finalized keep (bug #290d).
        // ============================================================

        [Fact]
        public void FinalizedSameIdPendingTree_RestoreAttemptNotClearedBeforeTheFinalizedKeep()
        {
            // A same-id pending tree already Finalized (bug #290d: the restore keeps it as
            // authoritative): the restore attempt armed for its committed original stays armed.
            var committed = MakeFutureTree("ql_finalized");
            foreach (var rec in committed.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(committed);
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, "test copy-on-write resume");
            var finalizedClone = RecordingTree.DeepClone(committed);
            RecordingStore.StashPendingTree(finalizedClone, PendingTreeState.Finalized);
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_finalized"), committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.Same(finalizedClone, RecordingStore.PendingTree);
            Assert.Contains(logLines, l => l.Contains("keeping in-memory Finalized tree"));
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
        }

        // ============================================================
        // The merge state of a member committed before the quicksave (owner ruling D2).
        // ============================================================

        [Fact]
        public void CommittedBeforeTheQuicksave_PromotedMemberKeepsItsCommittedMergeState()
        {
            // D2: the quicksave holds the tree as committed history, the restore keeps the
            // copy-on-write clone, and the reconcile reads every member as still committed.
            var committed = CommitFuture("ql_ms_d2", promoteBooster: true);
            RecordingTree clone = StashResumedClone(committed);
            string boosterId = BoosterId(committed);
            var quicksavedCommitted = MakeQuicksaveTree("ql_ms_d2");
            quicksavedCommitted.Recordings[boosterId].MergeState = MergeState.CommittedProvisional;
            var quicksavedActive = MakeQuicksaveTree("ql_ms_d2");
            quicksavedActive.Recordings[boosterId].MergeState = MergeState.CommittedProvisional;
            ConfigNode node = QuicksaveNode(quicksavedActive, quicksavedCommitted);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            Assert.Same(clone, RecordingStore.PendingTree);
            RecordingTree resumed = RecordingStore.PopPendingTree();
            RunResumePrep(resumed, QuicksaveUT, LoadKind.QuickloadFlight);

            Assert.Equal(MergeState.CommittedProvisional, resumed.Recordings[boosterId].MergeState);
            Assert.Equal(MergeState.CommittedProvisional, committed.Recordings[boosterId].MergeState);
            AssertNoLine("Quickload abandoned-future merge state reset:");
        }

        // ============================================================
        // Pure pieces
        // ============================================================

        [Fact]
        public void CaptureQuicksaveTreeFacts_TreeCommittedInQuicksaveFromCommittedTreeNodesOnly()
        {
            var active = MakeQuicksaveTree("ql_facts");
            var other = MakeQuicksaveTree("ql_facts_other");
            var pending = MakeQuicksaveTree("ql_facts_pending");

            var node = new ConfigNode("PARSEK_SCENARIO");
            other.Save(node.AddNode("RECORDING_TREE"));
            var pendingNode = node.AddNode("RECORDING_TREE");
            pending.Save(pendingNode);
            pendingNode.AddValue("isPending", "True");
            var activeNode = node.AddNode("RECORDING_TREE");
            active.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            HashSet<string> treeIds = ParsekScenario.CollectQuicksaveCommittedTreeIds(node);
            Assert.Equal(new[] { "ql_facts_other" }, treeIds.ToArray());
            var facts = ParsekScenario.CaptureQuicksaveTreeFacts(
                RecordingTree.Load(activeNode), ParsekScenario.CollectQuicksaveCommittedRecordingIds(node), treeIds);
            Assert.False(facts.TreeCommittedInQuicksave);

            active.Save(node.AddNode("RECORDING_TREE"));
            treeIds = ParsekScenario.CollectQuicksaveCommittedTreeIds(node);
            facts = ParsekScenario.CaptureQuicksaveTreeFacts(
                RecordingTree.Load(activeNode), ParsekScenario.CollectQuicksaveCommittedRecordingIds(node), treeIds);
            Assert.True(facts.TreeCommittedInQuicksave);
            Assert.False(ParsekScenario.CaptureQuicksaveTreeFacts(
                RecordingTree.Load(activeNode), null).TreeCommittedInQuicksave);
        }

        [Fact]
        public void TrimRecordingPastUT_CutsBodyFixedFramesAtTheCutoff()
        {
            // A parent-anchored Relative section the salvage or the refresh brings in from the
            // committed copy carries its body-fixed surface past the cutoff too.
            var rec = new Recording { RecordingId = "ql_bodyfixed" };
            SetPoints(rec, 100.0, 150.0, 300.0);
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 300.0,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = new List<TrajectoryPoint>(),
            };
            foreach (double ut in new[] { 100.0, 200.0, 250.0, 300.0 })
            {
                section.frames.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
                section.bodyFixedFrames.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            }
            rec.TrackSections.Add(section);

            Assert.True(ParsekScenario.TrimRecordingPastUT(rec, 200.0));

            TrackSection trimmed = rec.TrackSections[0];
            Assert.Equal(200.0, trimmed.endUT);
            Assert.Equal(new[] { 100.0, 200.0 }, trimmed.frames.Select(f => f.ut).ToArray());
            Assert.Equal(new[] { 100.0, 200.0 }, trimmed.bodyFixedFrames.Select(f => f.ut).ToArray());
        }

        // ============================================================
        // Helpers
        // ============================================================

        private void AssertNoLine(string fragment)
        {
            string found = logLines.FirstOrDefault(l => l.Contains(fragment));
            Assert.True(found == null, "unexpected log line: " + found);
        }

        private static string BoosterId(RecordingTree tree) => "child_" + tree.Id + "_1";

        // The flight as the quicksave at 200 holds it: the pod (root, active) and a booster,
        // both still recording, both named at sidecar epoch 1.
        private static RecordingTree MakeQuicksaveTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording { RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = RootPid };
            SetPoints(root, 100.0, 150.0, QuicksaveUT);
            tree.AddOrReplaceRecording(root);
            var booster = new Recording
            {
                RecordingId = BoosterId(tree), TreeId = id, VesselName = id + " booster", VesselPersistentId = BoosterPid,
            };
            SetPoints(booster, 120.0, 180.0, QuicksaveUT);
            tree.AddOrReplaceRecording(booster);
            foreach (var rec in tree.Recordings.Values)
            {
                rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
                rec.RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration;
                rec.SidecarEpoch = 1;
            }
            tree.RebuildBackgroundMap();
            return tree;
        }

        // The flight after the quicksave: the pod flew on to 320, the booster was destroyed at
        // 300 with Bob aboard. The OnSave after the commit wrote both at epoch 2.
        private static RecordingTree MakeFutureTree(string id)
        {
            var tree = MakeQuicksaveTree(id);
            SetPoints(tree.Recordings[tree.RootRecordingId], 100.0, 150.0, 320.0);
            var booster = tree.Recordings[BoosterId(tree)];
            SetPoints(booster, 120.0, 180.0, 300.0);
            booster.TerminalStateValue = TerminalState.Destroyed;
            booster.VesselDestroyed = true;
            booster.GhostVisualSnapshot = CrewSnapshot("Bob Kerman");
            booster.CrewEndStates = new Dictionary<string, KerbalEndState> { { "Bob Kerman", KerbalEndState.Dead } };
            booster.CrewEndStatesResolved = true;
            foreach (var rec in tree.Recordings.Values)
                rec.SidecarEpoch = 2;
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static RecordingTree CommitFuture(string id, bool promoteBooster = false)
        {
            var tree = MakeFutureTree(id);
            // The commit's promotion pass made the crashed booster an Unfinished Flight.
            if (promoteBooster)
                tree.Recordings[BoosterId(tree)].MergeState = MergeState.CommittedProvisional;
            CommitTreeWithSidecars(tree);
            return tree;
        }

        private static void CommitTreeWithSidecars(RecordingTree tree)
        {
            WriteSidecarsAtEpoch(tree, 2);
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // The copy-on-write clone the recorder resumed on the still-active pod after the
        // in-flight commit, carrying a post-commit tail; the F9's scene change stashed it.
        private static RecordingTree StashResumedClone(RecordingTree committed)
        {
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, "test copy-on-write resume");
            var clone = RecordingTree.DeepClone(committed);
            SetPoints(clone.Recordings[clone.RootRecordingId], 100.0, 150.0, 320.0, 340.0);
            RecordingStore.StashPendingTree(clone, PendingTreeState.Limbo);
            return clone;
        }

        private static void WriteSidecarsAtEpoch(RecordingTree tree, int epoch)
        {
            foreach (var rec in tree.Recordings.Values)
            {
                var onDisk = Recording.DeepClone(rec);
                onDisk.SidecarEpoch = epoch - 1;
                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    onDisk,
                    RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(onDisk.RecordingId)),
                    RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildVesselSnapshotRelativePath(onDisk.RecordingId)),
                    RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildGhostSnapshotRelativePath(onDisk.RecordingId)),
                    incrementEpoch: true));
                Assert.Equal(epoch, onDisk.SidecarEpoch);
            }
        }

        // A save node holding the active tree, and, when given, a committed tree node written
        // before it (OnSave writes committed trees first).
        private static ConfigNode QuicksaveNode(RecordingTree activeTree, RecordingTree committedTree)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            if (committedTree != null)
                committedTree.Save(node.AddNode("RECORDING_TREE"));
            var treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            return node;
        }

        private static ConfigNode CrewSnapshot(string kerbal)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", kerbal);
            return snapshot;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 1000.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static void RunResumePrep(RecordingTree tree, double resumeUT, LoadKind kind)
        {
            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => resumeUT;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree, kind);
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }
    }
}
