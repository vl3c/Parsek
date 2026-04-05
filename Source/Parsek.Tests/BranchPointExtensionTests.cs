using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BranchPointExtensionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BranchPointExtensionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        // --- Enum value stability ---

        [Fact]
        public void BranchPointType_Launch_HasValue5()
        {
            Assert.Equal(5, (int)BranchPointType.Launch);
        }

        [Fact]
        public void BranchPointType_Breakup_HasValue6()
        {
            Assert.Equal(6, (int)BranchPointType.Breakup);
        }

        [Fact]
        public void BranchPointType_Terminal_HasValue7()
        {
            Assert.Equal(7, (int)BranchPointType.Terminal);
        }

        // --- Round-trip: Breakup with all metadata ---

        [Fact]
        public void BranchPoint_Breakup_AllMetadata_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_breakup_1",
                UT = 17200.5,
                Type = BranchPointType.Breakup,
                ParentRecordingIds = new List<string> { "rec_parent" },
                ChildRecordingIds = new List<string> { "rec_debris1", "rec_debris2", "rec_debris3" },
                BreakupCause = "CRASH",
                BreakupDuration = 0.35,
                DebrisCount = 5,
                CoalesceWindow = 0.5
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_breakup_1", restored.Id);
            Assert.Equal(17200.5, restored.UT);
            Assert.Equal(BranchPointType.Breakup, restored.Type);
            Assert.Single(restored.ParentRecordingIds);
            Assert.Equal("rec_parent", restored.ParentRecordingIds[0]);
            Assert.Equal(3, restored.ChildRecordingIds.Count);
            Assert.Equal("CRASH", restored.BreakupCause);
            Assert.Equal(0.35, restored.BreakupDuration);
            Assert.Equal(5, restored.DebrisCount);
            Assert.Equal(0.5, restored.CoalesceWindow);
        }

        // --- Round-trip: Terminal with TerminalCause ---

        [Fact]
        public void BranchPoint_Terminal_WithCause_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_term_1",
                UT = 18500.0,
                Type = BranchPointType.Terminal,
                ParentRecordingIds = new List<string> { "rec_final" },
                TerminalCause = "RECOVERED"
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_term_1", restored.Id);
            Assert.Equal(18500.0, restored.UT);
            Assert.Equal(BranchPointType.Terminal, restored.Type);
            Assert.Equal("RECOVERED", restored.TerminalCause);
            Assert.Single(restored.ParentRecordingIds);
            Assert.Empty(restored.ChildRecordingIds);
        }

        // --- Round-trip: Launch with no extra metadata ---

        [Fact]
        public void BranchPoint_Launch_NoExtraMetadata_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_launch_1",
                UT = 17000.0,
                Type = BranchPointType.Launch,
                ChildRecordingIds = new List<string> { "rec_root" }
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_launch_1", restored.Id);
            Assert.Equal(17000.0, restored.UT);
            Assert.Equal(BranchPointType.Launch, restored.Type);
            Assert.Empty(restored.ParentRecordingIds);
            Assert.Single(restored.ChildRecordingIds);
            Assert.Equal("rec_root", restored.ChildRecordingIds[0]);

            // All metadata fields default
            Assert.Null(restored.SplitCause);
            Assert.Equal(0u, restored.DecouplerPartId);
            Assert.Null(restored.BreakupCause);
            Assert.Equal(0.0, restored.BreakupDuration);
            Assert.Equal(0, restored.DebrisCount);
            Assert.Equal(0.0, restored.CoalesceWindow);
            Assert.Null(restored.MergeCause);
            Assert.Equal(0u, restored.TargetVesselPersistentId);
            Assert.Null(restored.TerminalCause);
        }

        // --- Round-trip: Undock with SplitCause and DecouplerPartId ---

        [Fact]
        public void BranchPoint_Undock_WithSplitMetadata_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_undock_split",
                UT = 17100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_parent" },
                ChildRecordingIds = new List<string> { "rec_a", "rec_b" },
                SplitCause = "DECOUPLE",
                DecouplerPartId = 42001
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_undock_split", restored.Id);
            Assert.Equal(BranchPointType.Undock, restored.Type);
            Assert.Equal("DECOUPLE", restored.SplitCause);
            Assert.Equal(42001u, restored.DecouplerPartId);
        }

        // --- Round-trip: Dock with MergeCause and TargetVesselPersistentId ---

        [Fact]
        public void BranchPoint_Dock_WithMergeMetadata_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_dock_merge",
                UT = 18000.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { "rec_a", "rec_b" },
                ChildRecordingIds = new List<string> { "rec_merged" },
                MergeCause = "DOCK",
                TargetVesselPersistentId = 99887766
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_dock_merge", restored.Id);
            Assert.Equal(BranchPointType.Dock, restored.Type);
            Assert.Equal("DOCK", restored.MergeCause);
            Assert.Equal(99887766u, restored.TargetVesselPersistentId);
        }

        // --- Backward compatibility: old BranchPoint without new metadata ---

        [Fact]
        public void BranchPoint_OldFormat_WithoutNewMetadata_DefaultsToNullAndZero()
        {
            // Simulate an old-format node that only has the original fields
            var node = new ConfigNode("BRANCH_POINT");
            var ic = CultureInfo.InvariantCulture;
            node.AddValue("id", "bp_old");
            node.AddValue("ut", (17050.0).ToString("R", ic));
            node.AddValue("type", "0"); // Undock
            node.AddValue("parentId", "rec_p");
            node.AddValue("childId", "rec_c1");
            node.AddValue("childId", "rec_c2");

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("bp_old", restored.Id);
            Assert.Equal(17050.0, restored.UT);
            Assert.Equal(BranchPointType.Undock, restored.Type);
            Assert.Single(restored.ParentRecordingIds);
            Assert.Equal(2, restored.ChildRecordingIds.Count);

            // All new metadata fields default
            Assert.Null(restored.SplitCause);
            Assert.Equal(0u, restored.DecouplerPartId);
            Assert.Null(restored.BreakupCause);
            Assert.Equal(0.0, restored.BreakupDuration);
            Assert.Equal(0, restored.DebrisCount);
            Assert.Equal(0.0, restored.CoalesceWindow);
            Assert.Null(restored.MergeCause);
            Assert.Equal(0u, restored.TargetVesselPersistentId);
            Assert.Null(restored.TerminalCause);
        }

        // --- Forward tolerance: unknown type integer > 7 ---

        [Fact]
        public void BranchPoint_UnknownTypeInteger_DoesNotCrash_LogsWarning()
        {
            var node = new ConfigNode("BRANCH_POINT");
            var ic = CultureInfo.InvariantCulture;
            node.AddValue("id", "bp_future");
            node.AddValue("ut", (20000.0).ToString("R", ic));
            node.AddValue("type", "99"); // Unknown future type
            node.AddValue("parentId", "rec_p");

            var restored = RecordingTree.LoadBranchPointFrom(node);

            // Should not crash, should default to Undock
            Assert.Equal("bp_future", restored.Id);
            Assert.Equal(BranchPointType.Undock, restored.Type);
            Assert.Single(restored.ParentRecordingIds);

            // Should have logged a warning about unknown type
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("unknown type integer 99"));
        }

        // --- ToString includes new type names ---

        [Theory]
        [InlineData(BranchPointType.Launch, "Launch")]
        [InlineData(BranchPointType.Breakup, "Breakup")]
        [InlineData(BranchPointType.Terminal, "Terminal")]
        public void BranchPoint_ToString_IncludesNewTypeName(BranchPointType type, string expectedName)
        {
            var bp = new BranchPoint
            {
                Id = "bp_test",
                UT = 1000.0,
                Type = type
            };

            string result = bp.ToString();

            Assert.Contains($"type={expectedName}", result);
            Assert.Contains("id=bp_test", result);
        }

        // --- Log assertion: Breakup save logs metadata ---

        [Fact]
        public void BranchPoint_Save_Breakup_LogsMetadata()
        {
            var bp = new BranchPoint
            {
                Id = "bp_breakup_log",
                UT = 17300.0,
                Type = BranchPointType.Breakup,
                ParentRecordingIds = new List<string> { "rec_p" },
                ChildRecordingIds = new List<string> { "rec_c1", "rec_c2" },
                BreakupCause = "OVERHEAT",
                DebrisCount = 3,
                BreakupDuration = 0.42,
                CoalesceWindow = 0.5
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("SaveBranchPoint") &&
                l.Contains("breakupCause=OVERHEAT"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("SaveBranchPoint") &&
                l.Contains("debrisCount=3"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("SaveBranchPoint") &&
                l.Contains("breakupDuration=0.420"));
        }

        // --- Log assertion: Load Breakup logs metadata ---

        [Fact]
        public void BranchPoint_Load_Breakup_LogsMetadata()
        {
            var bp = new BranchPoint
            {
                Id = "bp_breakup_load_log",
                UT = 17400.0,
                Type = BranchPointType.Breakup,
                BreakupCause = "STRUCTURAL_FAILURE",
                DebrisCount = 7,
                BreakupDuration = 1.2,
                CoalesceWindow = 0.5
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            logLines.Clear();

            RecordingTree.LoadBranchPointFrom(node);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("LoadBranchPoint") &&
                l.Contains("breakupCause=STRUCTURAL_FAILURE"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("LoadBranchPoint") &&
                l.Contains("debrisCount=7"));
        }

        // --- Breakup: OVERHEAT cause round-trips ---

        [Fact]
        public void BranchPoint_Breakup_OverheatCause_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_overheat",
                UT = 17500.0,
                Type = BranchPointType.Breakup,
                BreakupCause = "OVERHEAT",
                BreakupDuration = 0.1,
                DebrisCount = 2,
                CoalesceWindow = 0.5
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal("OVERHEAT", restored.BreakupCause);
            Assert.Equal(0.1, restored.BreakupDuration);
            Assert.Equal(2, restored.DebrisCount);
            Assert.Equal(0.5, restored.CoalesceWindow);
        }

        // --- Terminal: DESTROYED cause round-trips ---

        [Fact]
        public void BranchPoint_Terminal_DestroyedCause_RoundTrips()
        {
            var bp = new BranchPoint
            {
                Id = "bp_destroyed",
                UT = 19000.0,
                Type = BranchPointType.Terminal,
                ParentRecordingIds = new List<string> { "rec_doom" },
                TerminalCause = "DESTROYED"
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal(BranchPointType.Terminal, restored.Type);
            Assert.Equal("DESTROYED", restored.TerminalCause);
        }

        // --- Selective serialization: zero/null values are omitted ---

        [Fact]
        public void BranchPoint_Save_OmitsDefaultValues()
        {
            var bp = new BranchPoint
            {
                Id = "bp_minimal",
                UT = 17000.0,
                Type = BranchPointType.Launch
                // All metadata fields left at defaults (null/0)
            };

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            // Verify none of the metadata keys were written
            Assert.Null(node.GetValue("splitCause"));
            Assert.Null(node.GetValue("decouplerPartId"));
            Assert.Null(node.GetValue("breakupCause"));
            Assert.Null(node.GetValue("breakupDuration"));
            Assert.Null(node.GetValue("debrisCount"));
            Assert.Null(node.GetValue("coalesceWindow"));
            Assert.Null(node.GetValue("mergeCause"));
            Assert.Null(node.GetValue("targetVesselPid"));
            Assert.Null(node.GetValue("terminalCause"));
        }
    }
}
