using System;
using System.Collections.Generic;
using System.IO;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The in-game Re-Fly merge cells that author their own recordings drive the real
    /// <c>MergeJournalOrchestrator.RunMerge</c>, whose post-merge optimizer pass flushes every
    /// dirty recording to the live save. The binary sidecar writer refuses a point with no body
    /// name, so such a fixture logs an ERROR and writes nothing (R7c `2026-11-23_1529`: HEAD and
    /// TIP of both spanning cells). These cells write the real fixtures' HEAD, TIP and fork
    /// through the production sidecar writer.
    /// </summary>
    [Collection("Sequential")]
    public class MergeFixtureSidecarFlushTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool? priorMirrorOverride;

        public MergeFixtureSidecarFlushTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_mergefix_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorMirrorOverride = RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = true;
        }

        public void Dispose()
        {
            RecordingSidecarStore.WriteReadableSidecarMirrorsOverrideForTesting = priorMirrorOverride;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            try { Directory.Delete(tempDir, true); }
            catch (IOException) { }
        }

        private bool Save(Recording rec)
        {
            return RecordingSidecarStore.SaveRecordingFilesToPathsForTesting(
                rec,
                Path.Combine(tempDir, rec.RecordingId + ".prec"),
                Path.Combine(tempDir, rec.RecordingId + "_vessel.craft"),
                Path.Combine(tempDir, rec.RecordingId + "_ghost.craft"));
        }

        private void AssertFlushesClean(Recording origin, Recording fork, double rewindUT)
        {
            Recording tip = RecordingOptimizer.SplitAtUT(origin, rewindUT);
            Assert.NotNull(tip);
            tip.RecordingId = origin.RecordingId + "_tip";
            foreach (Recording rec in new[] { origin, tip, fork })
            {
                Assert.True(Save(rec), rec.RecordingId + " did not save: "
                    + string.Join("\n", logLines.FindAll(l => l.Contains("[ERROR]")).ToArray()));
                Assert.True(File.Exists(Path.Combine(tempDir, rec.RecordingId + ".prec")));
            }
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
        }

        [Fact]
        public void SpannedSplitFixture_HeadTipAndForkWriteTheirSidecars()
        {
            AssertFlushesClean(
                IncompleteBallisticRuntimeTests.BuildSpannedSplitOrigin("ss_origin", "ss_tree", 8.42, 34.24, 52.7),
                IncompleteBallisticRuntimeTests.BuildSpannedSplitFork(
                    "ss_fork", "ss_tree", "ss_origin", "ss_sess", "ss_bp", "ss_rp"),
                34.24);
        }

        [Fact]
        public void RecoveryBundleFixture_HeadTipAndForkWriteTheirSidecars()
        {
            AssertFlushesClean(
                ReFlyRecoveryBundleRuntimeTest.BuildOriginRecording(
                    "rb_origin", "rb_tree", 987654321u, Guid.NewGuid().ToString()),
                ReFlyRecoveryBundleRuntimeTest.BuildForkRecording(
                    "rb_fork", "rb_tree", "rb_origin", "rb_sess", "rb_bp", "rb_rp"),
                340.0);
        }

        // The same fixtures through the real RunMerge and the real post-merge pass: naming the
        // body changes neither the cut nor the rows the in-game cells assert on.
        [Theory]
        [InlineData(true, 8.42, 34.24, 52.7)]
        [InlineData(false, 330.0, 340.0, 348.08)]
        public void RunMerge_WithTheRealPass_KeepsHeadAndTipApart(
            bool spanned, double startUT, double rewindUT, double endUT)
        {
            Recording origin = spanned
                ? IncompleteBallisticRuntimeTests.BuildSpannedSplitOrigin("fx_origin", "fx_tree", startUT, rewindUT, endUT)
                : ReFlyRecoveryBundleRuntimeTest.BuildOriginRecording(
                    "fx_origin", "fx_tree", 987654321u, Guid.NewGuid().ToString());
            Recording fork = spanned
                ? IncompleteBallisticRuntimeTests.BuildSpannedSplitFork(
                    "fx_fork", "fx_tree", "fx_origin", "fx_sess", "fx_bp", "fx_rp")
                : ReFlyRecoveryBundleRuntimeTest.BuildForkRecording(
                    "fx_fork", "fx_tree", "fx_origin", "fx_sess", "fx_bp", "fx_rp");

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            MergeJournalOrchestrator.ResetTestOverrides();
            MergeJournalOrchestrator.DurableSaveForTesting = label => { };
            try
            {
                var tree = new RecordingTree
                {
                    Id = "fx_tree", TreeName = "fx_tree", RootRecordingId = "fx_origin",
                    ActiveRecordingId = "fx_origin",
                    BranchPoints = new List<BranchPoint>
                    {
                        new BranchPoint
                        {
                            Id = "fx_bp", Type = BranchPointType.Undock, UT = rewindUT, RewindPointId = "fx_rp",
                            ParentRecordingIds = new List<string> { "fx_upstream" },
                            ChildRecordingIds = new List<string> { "fx_origin", "fx_fork" },
                        },
                    },
                };
                foreach (Recording rec in new[] { origin, fork })
                {
                    tree.AddOrReplaceRecording(rec);
                    RecordingStore.AddRecordingWithTreeForTesting(rec, "fx_tree");
                }
                var trees = RecordingStore.CommittedTrees;
                for (int i = trees.Count - 1; i >= 0; i--)
                    if (trees[i].Id == "fx_tree") trees.RemoveAt(i);
                trees.Add(tree);
                var marker = new ReFlySessionMarker
                {
                    SessionId = "fx_sess", TreeId = "fx_tree", ActiveReFlyRecordingId = "fx_fork",
                    OriginChildRecordingId = "fx_origin", SupersedeTargetId = "fx_origin",
                    RewindPointId = "fx_rp", RewindPointUT = rewindUT, InvokedUT = rewindUT,
                };
                var scenario = new ParsekScenario
                {
                    RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                    LedgerTombstones = new List<LedgerTombstone>(),
                    RewindPoints = new List<RewindPoint>
                    {
                        new RewindPoint
                        {
                            RewindPointId = "fx_rp", BranchPointId = "fx_bp", UT = rewindUT,
                            SessionProvisional = true, CreatingSessionId = "fx_sess", FocusSlotIndex = 0,
                            ChildSlots = new List<ChildSlot>
                            {
                                new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "fx_origin", Controllable = true },
                            },
                        },
                    },
                    ActiveReFlySessionMarker = marker,
                };
                ParsekScenario.SetInstanceForTesting(scenario);
                scenario.BumpSupersedeStateVersion();
                EffectiveState.ResetCachesForTesting();

                Assert.True(MergeJournalOrchestrator.RunMerge(marker, fork));

                Assert.Contains(logLines, l => l.Contains("Post-merge optimization pass ran sess=fx_sess route=merge"));
                Recording head = null, tip = null;
                foreach (Recording rec in RecordingStore.CommittedRecordings)
                {
                    if (rec.RecordingId == "fx_origin") head = rec;
                    else if (rec.RecordingId != "fx_fork" && rec.ChainIndex == 1) tip = rec;
                }
                Assert.NotNull(head);
                Assert.NotNull(tip);
                Assert.Equal(3, RecordingStore.CommittedRecordings.Count);
                Assert.Equal(startUT, head.StartUT, 3);
                Assert.Equal(rewindUT, head.EndUT, 3);
                Assert.Equal(rewindUT, tip.StartUT, 3);
                Assert.Equal(endUT, tip.EndUT, 3);
                var row = Assert.Single(scenario.RecordingSupersedes);
                Assert.Equal(tip.RecordingId, row.OldRecordingId);
                Assert.Equal("fx_fork", row.NewRecordingId);
                Assert.DoesNotContain(logLines, l => l.Contains("Non-null string expected"));
            }
            finally
            {
                MergeJournalOrchestrator.ResetTestOverrides();
                RecordingStore.ResetForTesting();
                Ledger.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();
                SessionSuppressionState.ResetForTesting();
            }
        }

        // The R7c red itself: the same writer refuses a point with no body name.
        [Fact]
        public void APointWithNoBodyName_IsRefused()
        {
            var rec = new Recording { RecordingId = "nobody" };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0 });
            Assert.False(Save(rec));
            Assert.Contains(logLines, l => l.Contains("[Parsek][ERROR][RecordingStore]")
                && l.Contains("Non-null string expected in binary string table"));
        }
    }
}
