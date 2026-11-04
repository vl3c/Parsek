using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The 1.0 save-format registry (<see cref="SaveFormat"/>): the pure classification, the
    /// upgrade-step lookup, the save-wide stamp, and the upgrade lane the recording gate, the
    /// sidecar probes and the tree loader route an Upgradable stamp through. Nothing is
    /// Upgradable today (every Current equals its Baseline), so the lane is exercised by raising a
    /// format's current value through the test seam and registering a test step.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFormatTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private static readonly int Gen = RecordingStore.CurrentRecordingSchemaGeneration;
        private static readonly int Fmt = RecordingStore.CurrentRecordingFormatVersion;

        public SaveFormatTests()
        {
            SaveFormat.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            InertRecordingTrees.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            SaveFormat.ResetForTesting();
            InertRecordingTrees.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static SaveFormatUpgradeStep MarkerStep(int from, string marker)
        {
            return new SaveFormatUpgradeStep
            {
                From = from,
                Description = "test step " + marker,
                UpgradeNode = n => { n.AddValue("upgradedBy", marker); return true; },
            };
        }

        // ---------------------------------------------------------------- Classify

        [Theory]
        [InlineData(null, 4, 4, "Missing")]
        [InlineData(3, 4, 4, "PreBaseline")]
        [InlineData(0, 4, 4, "PreBaseline")]
        [InlineData(4, 4, 4, "Current")]
        [InlineData(5, 4, 4, "Newer")]
        [InlineData(4, 4, 6, "Upgradable")]
        [InlineData(5, 4, 6, "Upgradable")]
        [InlineData(6, 4, 6, "Current")]
        [InlineData(7, 4, 6, "Newer")]
        [InlineData(3, 4, 6, "PreBaseline")]
        [InlineData(-1, 0, 0, "PreBaseline")]
        [InlineData(0, 0, 0, "Current")]
        public void Classify_PlacesStoredAgainstBaselineAndCurrent(int? stored, int baseline, int current,
            string expected)
        {
            Assert.Equal(expected, SaveFormat.Classify(stored, baseline, current).ToString());
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("abc", null)]
        [InlineData("4", 4)]
        [InlineData("-2", -2)]
        public void ParseStored_IsInvariantAndNullOnGarbage(string raw, int? expected)
        {
            Assert.Equal(expected, SaveFormat.ParseStored(raw));
        }

        // ---------------------------------------------------------------- registry

        [Fact]
        public void Registry_RowsReferenceTheLiveConstants_AndTheBaselineIsToday()
        {
            Assert.Equal(RecordingSchema.CurrentRecordingFormatVersion, SaveFormat.RecordingFormat.Current);
            Assert.Equal(RecordingSchema.CurrentRecordingSchemaGeneration, SaveFormat.RecordingSchemaGeneration.Current);
            Assert.Equal(RecordingTree.CurrentTreeFormatVersion, SaveFormat.TreeFormat.Current);
            Assert.Equal(TrajectorySidecarBinary.CurrentBinaryVersion, SaveFormat.TrajectoryBinary.Current);
            Assert.Equal(SnapshotSidecarCodec.CurrentFormatVersion, SaveFormat.Snapshot.Current);
            Assert.Equal(Ledger.CurrentLedgerVersion, SaveFormat.Ledger.Current);
            Assert.Equal(GameStateStore.CurrentEventsVersion, SaveFormat.GameStateStoreFormat.Current);
            Assert.Equal(MilestoneStore.CurrentMilestonesVersion, SaveFormat.Milestones.Current);
            Assert.Equal(Mission.CurrentSelectionSchemaGeneration, SaveFormat.MissionSelection.Current);
            Assert.Equal(SaveFormat.CurrentSaveFormat, SaveFormat.Save.Current);

            // The 1.0 baseline (owner ruling 2026-11-04): today's values, so nothing is Upgradable.
            var baselines = SaveFormat.All.ToDictionary(f => f.Name, f => f.Baseline);
            Assert.Equal(1, baselines["save"]);
            Assert.Equal(1, baselines["recording-format"]);
            Assert.Equal(4, baselines["recording-schema-generation"]);
            Assert.Equal(0, baselines["tree-format"]);
            Assert.Equal(1, baselines["trajectory-binary"]);
            Assert.Equal(0, baselines["snapshot-sidecar"]);
            Assert.Equal(0, baselines["ledger"]);
            Assert.Equal(1, baselines["game-state-store"]);
            Assert.Equal(1, baselines["milestones"]);
            Assert.Equal(1, baselines["mission-selection"]);
            Assert.Equal(10, SaveFormat.All.Count);
            Assert.Equal(SaveFormat.All.Count, SaveFormat.All.Select(f => f.Name).Distinct().Count());
        }

        [Fact]
        public void Registry_EveryFormatAtBaseline_HasNoProductionSteps()
        {
            foreach (SaveFormatInfo f in SaveFormat.All)
            {
                Assert.True(f.Current >= f.Baseline, f.Name + ": current below the 1.0 baseline");
                // A bump past the baseline must register its steps in the same commit.
                Assert.Equal(f.Current - f.Baseline, SaveFormat.RegisteredStepCount(f));
            }
        }

        [Fact]
        public void UpgradePath_NeedsEveryStepFromStoredToCurrent()
        {
            SaveFormatInfo gen = SaveFormat.RecordingSchemaGeneration;
            SaveFormat.SetCurrentForTesting(gen, Gen + 2);

            Assert.False(SaveFormat.TryGetUpgradePath(gen, Gen, out _));
            SaveFormat.RegisterStepForTesting(gen, MarkerStep(Gen + 1, "b"));
            Assert.False(SaveFormat.TryGetUpgradePath(gen, Gen, out _));
            Assert.True(SaveFormat.TryGetUpgradePath(gen, Gen + 1, out List<SaveFormatUpgradeStep> tail));
            Assert.Single(tail);

            SaveFormat.RegisterStepForTesting(gen, MarkerStep(Gen, "a"));
            Assert.True(SaveFormat.TryGetUpgradePath(gen, Gen, out List<SaveFormatUpgradeStep> path));
            Assert.Equal(new[] { Gen, Gen + 1 }, path.Select(s => s.From).ToArray());

            // Pre-baseline, current and newer data never get a path.
            Assert.False(SaveFormat.TryGetUpgradePath(gen, Gen - 1, out _));
            Assert.False(SaveFormat.TryGetUpgradePath(gen, Gen + 2, out _));
            Assert.False(SaveFormat.TryGetUpgradePath(gen, Gen + 3, out _));
        }

        [Fact]
        public void TryUpgradeNode_RunsStepsInOrder_AndReportsARefusal()
        {
            SaveFormatInfo ledger = SaveFormat.Ledger;
            SaveFormat.SetCurrentForTesting(ledger, ledger.Baseline + 1);
            SaveFormat.RegisterStepForTesting(ledger, MarkerStep(ledger.Baseline, "one"));
            var node = new ConfigNode("LEDGER");

            Assert.True(SaveFormat.TryUpgradeNode(ledger, ledger.Baseline, node, "test", out string failure));
            Assert.Null(failure);
            Assert.Equal("one", node.GetValue("upgradedBy"));
            Assert.Contains(logLines, l => l.Contains("[SaveFormat]") && l.Contains("Upgraded ledger test from 0 to 1"));

            SaveFormat.ResetForTesting();
            SaveFormat.SetCurrentForTesting(ledger, ledger.Baseline + 1);
            SaveFormat.RegisterStepForTesting(ledger, new SaveFormatUpgradeStep
            {
                From = ledger.Baseline,
                UpgradeNode = n => false,
            });
            Assert.False(SaveFormat.TryUpgradeNode(ledger, ledger.Baseline, new ConfigNode("LEDGER"), "test", out failure));
            Assert.Equal("step-refused", failure);
            Assert.Contains(logLines, l => l.Contains("[SaveFormat]") && l.Contains("failed at step 0->1"));
        }

        // ---------------------------------------------------------------- save-wide stamp

        [Fact]
        public void StampScenarioNode_WritesTheFormatAndTheWriter_Once()
        {
            var node = new ConfigNode("SCENARIO");
            SaveFormat.StampScenarioNode(node);
            SaveFormat.StampScenarioNode(node);

            Assert.Single(node.GetValues(SaveFormat.SaveFormatKey));
            Assert.Equal("1", node.GetValue(SaveFormat.SaveFormatKey));
            Assert.Single(node.GetValues(SaveFormat.WrittenByKey));
            Assert.Equal(typeof(SaveFormat).Assembly.GetName().Version.ToString(), node.GetValue(SaveFormat.WrittenByKey));
        }

        [Fact]
        public void LoadedStamp_Absent_OnAParsekSave_IsOneInfoLine()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddNode("RECORDING_TREE");

            Assert.Equal(SaveFormatClass.Missing, SaveFormat.ClassifyLoadedScenarioNode(node, hasParsekData: true));
            Assert.Single(logLines, l => l.Contains("[INFO][SaveFormat]")
                && l.Contains("pre-1.0 save (no parsekSaveFormat); loading best effort"));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][SaveFormat]"));
        }

        [Fact]
        public void LoadedStamp_AbsentOnAnEmptyNode_SaysNothingAtInfo()
        {
            SaveFormat.ClassifyLoadedScenarioNode(new ConfigNode("SCENARIO"), hasParsekData: false);
            Assert.DoesNotContain(logLines, l => l.Contains("pre-1.0 save"));
        }

        [Fact]
        public void LoadedStamp_Current_IsVerbose()
        {
            var node = new ConfigNode("SCENARIO");
            SaveFormat.StampScenarioNode(node);

            Assert.Equal(SaveFormatClass.Current, SaveFormat.ClassifyLoadedScenarioNode(node, hasParsekData: true));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][SaveFormat]") && l.Contains("parsekSaveFormat=1 (current)"));
            Assert.DoesNotContain(logLines, l => l.Contains("[INFO][SaveFormat]") || l.Contains("[WARN][SaveFormat]"));
        }

        [Fact]
        public void LoadedStamp_Newer_IsOneWarnNamingBothNumbers()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue(SaveFormat.SaveFormatKey, "3");
            node.AddValue(SaveFormat.WrittenByKey, "9.9.9.0");

            Assert.Equal(SaveFormatClass.Newer, SaveFormat.ClassifyLoadedScenarioNode(node, hasParsekData: true));
            Assert.Single(logLines, l => l.Contains("[WARN][SaveFormat]")
                && l.Contains("parsekSaveFormat=3 > current=1") && l.Contains("writtenBy=9.9.9.0"));
        }

        // ---------------------------------------------------------------- recording gate

        [Theory]
        [InlineData(0, null, "generation-missing")]
        [InlineData(-1, null, "generation-older")]
        [InlineData(1, null, "generation-newer")]
        [InlineData(0, 0, null)]
        [InlineData(0, -1, "format-version-mismatch")]
        [InlineData(0, 1, "format-version-mismatch")]
        public void RecordingGate_Today_KeepsItsReasons(int generationOffset, int? formatOffset, string reason)
        {
            // generationOffset 0 with formatOffset null means a missing generation (stored 0).
            int generation = formatOffset == null && generationOffset == 0 ? 0 : Gen + generationOffset;
            int format = Fmt + (formatOffset ?? 0);

            bool ok = RecordingStore.IsRecordingSchemaCompatible(format, generation, out string actual, out bool upgrade);

            Assert.Equal(reason == null, ok);
            Assert.Equal(reason, actual);
            Assert.False(upgrade);
        }

        [Fact]
        public void RecordingGate_UpgradableGeneration_RoutesToTheStepLookup()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.RecordingSchemaGeneration, Gen + 1);

            // No registered step: an Upgradable stamp cannot be read, so it is rejected as older.
            Assert.False(RecordingStore.IsRecordingSchemaCompatible(Fmt, Gen, out string noPath, out _));
            Assert.Equal("generation-older", noPath);

            SaveFormat.RegisterStepForTesting(SaveFormat.RecordingSchemaGeneration, MarkerStep(Gen, "g"));
            Assert.True(RecordingStore.IsRecordingSchemaCompatible(Fmt, Gen, out string reason, out bool upgrade));
            Assert.Null(reason);
            Assert.True(upgrade);

            // Below the baseline stays rejected; the raised current reads as current; above it is newer.
            Assert.False(RecordingStore.IsRecordingSchemaCompatible(Fmt, Gen - 1, out string older, out _));
            Assert.Equal("generation-older", older);
            Assert.True(RecordingStore.IsRecordingSchemaCompatible(Fmt, Gen + 1, out _, out bool currentUpgrade));
            Assert.False(currentUpgrade);
            Assert.False(RecordingStore.IsRecordingSchemaCompatible(Fmt, Gen + 2, out string newer, out _));
            Assert.Equal("generation-newer", newer);
        }

        private static ConfigNode RecordingNode(string id)
        {
            var tree = new RecordingTree { Id = "sf-tree", TreeName = "SF", RootRecordingId = id };
            tree.AddOrReplaceRecording(new Recording { RecordingId = id, VesselName = "V " + id, TreeId = "sf-tree" });
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node.GetNodes("RECORDING")[0];
        }

        [Fact]
        public void LoadRecordingFrom_Upgradable_RunsTheStepsOnACopy_AndStampsCurrent()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.RecordingSchemaGeneration, Gen + 1);
            bool stepSawNode = false;
            SaveFormat.RegisterStepForTesting(SaveFormat.RecordingSchemaGeneration, new SaveFormatUpgradeStep
            {
                From = Gen,
                UpgradeNode = n =>
                {
                    stepSawNode = n.GetValue("recordingId") == "sf-rec";
                    n.SetValue("vesselName", "Renamed By Step", true);
                    return true;
                },
            });
            ConfigNode recNode = RecordingNode("sf-rec");
            string before = recNode.ToString();

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(recNode, rec);

            Assert.True(stepSawNode);
            Assert.NotEqual(-1, rec.RecordingFormatVersion);
            Assert.Equal(Gen + 1, rec.RecordingSchemaGeneration);
            Assert.Equal("Renamed By Step", rec.VesselName);
            Assert.Equal(before, recNode.ToString());
        }

        [Fact]
        public void LoadRecordingFrom_UpgradeStepRefuses_RejectsTheRecording()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.RecordingSchemaGeneration, Gen + 1);
            SaveFormat.RegisterStepForTesting(SaveFormat.RecordingSchemaGeneration,
                new SaveFormatUpgradeStep { From = Gen, UpgradeNode = n => false });
            RecordingStore.SuppressLogging = false;

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(RecordingNode("sf-rec"), rec);

            Assert.Equal(-1, rec.RecordingFormatVersion);
            Assert.Contains(logLines, l => l.Contains("rejecting recording sf-rec")
                && l.Contains("reason=generation-upgrade-failed step-refused"));
        }

        [Fact]
        public void SidecarProbeGates_RouteUpgradableThroughTheLookup()
        {
            Assert.Null(TrajectorySidecarBinary.BuildUnsupportedReason(Fmt, Gen));
            Assert.Null(SnapshotSidecarCodec.BuildGateReason(SnapshotSidecarCodec.CurrentFormatVersion, Gen));
            Assert.Equal("generation-newer", TrajectorySidecarBinary.BuildUnsupportedReason(Fmt, Gen + 1));
            Assert.Equal("format-version-mismatch", TrajectorySidecarBinary.BuildUnsupportedReason(Fmt + 1, Gen));
            Assert.StartsWith("format-version-mismatch",
                SnapshotSidecarCodec.BuildGateReason(SnapshotSidecarCodec.CurrentFormatVersion + 1, Gen));

            SaveFormat.SetCurrentForTesting(SaveFormat.RecordingSchemaGeneration, Gen + 1);
            Assert.Equal("generation-older", TrajectorySidecarBinary.BuildUnsupportedReason(Fmt, Gen));
            Assert.Equal("generation-older", SnapshotSidecarCodec.BuildGateReason(SnapshotSidecarCodec.CurrentFormatVersion, Gen));

            SaveFormat.RegisterStepForTesting(SaveFormat.RecordingSchemaGeneration, MarkerStep(Gen, "g"));
            Assert.Null(TrajectorySidecarBinary.BuildUnsupportedReason(Fmt, Gen));
            Assert.Null(SnapshotSidecarCodec.BuildGateReason(SnapshotSidecarCodec.CurrentFormatVersion, Gen));
        }

        [Fact]
        public void SidecarStamps_AgreeWithUpgradedMetadata_OnlyThroughTheLane()
        {
            Assert.True(RecordingSidecarStore.SidecarStampsAgreeWithMetadata(Fmt, Gen, Fmt, Gen));
            Assert.False(RecordingSidecarStore.SidecarStampsAgreeWithMetadata(Fmt, Gen - 1, Fmt, Gen));

            SaveFormat.SetCurrentForTesting(SaveFormat.RecordingSchemaGeneration, Gen + 1);
            // Metadata upgraded to the (raised) current, sidecar still at the stored generation.
            Assert.False(RecordingSidecarStore.SidecarStampsAgreeWithMetadata(Fmt, Gen, Fmt, Gen + 1));
            SaveFormat.RegisterStepForTesting(SaveFormat.RecordingSchemaGeneration, MarkerStep(Gen, "g"));
            Assert.True(RecordingSidecarStore.SidecarStampsAgreeWithMetadata(Fmt, Gen, Fmt, Gen + 1));
            // A sidecar newer than the metadata never agrees.
            Assert.False(RecordingSidecarStore.SidecarStampsAgreeWithMetadata(Fmt, Gen + 2, Fmt, Gen + 1));
        }

        // ---------------------------------------------------------------- tree format

        [Theory]
        [InlineData(null, null)]
        [InlineData(-1, null)]
        [InlineData(0, null)]
        [InlineData(1, "tree-format-newer")]
        [InlineData(7, "tree-format-newer")]
        public void TreeFormatRejectReason_Today(int? stored, string expected)
        {
            Assert.Equal(expected, RecordingTree.TreeFormatRejectReason(stored, out bool upgrade));
            Assert.False(upgrade);
        }

        [Fact]
        public void TreeFormatRejectReason_UpgradableNeedsAStep()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.TreeFormat, RecordingTree.CurrentTreeFormatVersion + 1);
            Assert.Equal("tree-format-older",
                RecordingTree.TreeFormatRejectReason(RecordingTree.CurrentTreeFormatVersion, out _));

            SaveFormat.RegisterStepForTesting(SaveFormat.TreeFormat,
                MarkerStep(RecordingTree.CurrentTreeFormatVersion, "t"));
            Assert.Null(RecordingTree.TreeFormatRejectReason(RecordingTree.CurrentTreeFormatVersion, out bool upgrade));
            Assert.True(upgrade);
        }

        private static ConfigNode TreeNode(string treeId, params string[] recordingIds)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = "Tree " + treeId, RootRecordingId = recordingIds[0] };
            for (int i = 0; i < recordingIds.Length; i++)
                tree.AddOrReplaceRecording(new Recording
                {
                    RecordingId = recordingIds[i],
                    VesselName = "Vessel " + recordingIds[i],
                    TreeId = treeId,
                    TreeOrder = i,
                });
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        [Fact]
        public void RecordingTreeLoad_NewerTreeFormat_LoadsNothing()
        {
            ConfigNode node = TreeNode("sf-tree-newer", "sf-r1");
            node.SetValue("treeFormatVersion", "1");

            RecordingTree tree = RecordingTree.Load(node);

            Assert.Empty(tree.Recordings);
            Assert.Contains(logLines, l => l.Contains("[RecordingTree]")
                && l.Contains("skipped tree=sf-tree-newer") && l.Contains("reason=tree-format-newer"));
        }

        [Fact]
        public void CommittedTreeWithNewerTreeFormat_IsKeptInert_AndSavedVerbatim()
        {
            ConfigNode original = TreeNode("sf-tree-newer", "sf-r1", "sf-r2");
            original.SetValue("treeFormatVersion", "1");
            original.AddValue("futureTreeKey", "kept");
            var scenario = new ConfigNode("SCENARIO");
            scenario.AddNode(original.CreateCopy());

            ParsekScenario.LoadRecordingTrees(scenario, RecordingStore.CommittedRecordings);

            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.Equal(1, InertRecordingTrees.Count);
            Assert.Equal("tree-format-newer", InertRecordingTrees.Entries[0].Reason);
            Assert.True(InertRecordingTrees.ContainsRecordingId("sf-r2"));

            var saved = new ConfigNode("SCENARIO");
            ParsekScenario.SaveTreeRecordings(saved);
            ConfigNode[] trees = saved.GetNodes("RECORDING_TREE");
            Assert.Single(trees);
            Assert.Equal(original.ToString(), trees[0].ToString());
        }

        [Fact]
        public void RecordingTreeLoad_UpgradableTreeFormat_RunsTheStepAndLoads()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.TreeFormat, RecordingTree.CurrentTreeFormatVersion + 1);
            SaveFormat.RegisterStepForTesting(SaveFormat.TreeFormat, new SaveFormatUpgradeStep
            {
                From = RecordingTree.CurrentTreeFormatVersion,
                UpgradeNode = n => { n.SetValue("treeName", "Upgraded", true); return true; },
            });
            ConfigNode node = TreeNode("sf-tree-up", "sf-r1");
            string before = node.ToString();

            RecordingTree tree = RecordingTree.Load(node);

            Assert.Single(tree.Recordings);
            Assert.Equal("Upgraded", tree.TreeName);
            Assert.Equal(RecordingTree.CurrentTreeFormatVersion + 1, tree.TreeFormatVersion);
            Assert.Equal(before, node.ToString());
            Assert.Contains(logLines, l => l.Contains("[SaveFormat]") && l.Contains("Upgraded tree-format tree sf-tree-up"));
        }
    }
}
