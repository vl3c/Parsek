using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the pure static helpers of <see cref="RecordingsTableUI"/>
    /// that no test called directly: the aggregate main-row pick, the root-row comparer, the
    /// EVA parent vessel name lookup and the visual-status text and tooltip.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableUICharacterizationTests : IDisposable
    {
        public RecordingsTableUICharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        private static Recording At(string id, double startUT, bool debris = false) =>
            new Recording { RecordingId = id, VesselName = id, ExplicitStartUT = startUT, IsDebris = debris };

        // ---------- FindAggregateMainRecordingIndex -----------------------------

        [Fact]
        public void FindAggregateMainRecordingIndex_EarliestNonDebrisMember()
        {
            var committed = new List<Recording>
            {
                At("late", 300.0),
                At("debris_early", 10.0, debris: true),
                At("early", 100.0),
                null,
                At("tied", 100.0),
            };
            // Out-of-range and null members are skipped; a tie keeps the first seen.
            Assert.Equal(2, RecordingsTableUI.FindAggregateMainRecordingIndex(
                new[] { -1, 0, 1, 2, 3, 4, 99 }, committed));
            Assert.Equal(4, RecordingsTableUI.FindAggregateMainRecordingIndex(
                new[] { 4, 2 }, committed));
        }

        [Fact]
        public void FindAggregateMainRecordingIndex_NoEligibleMember_MinusOne()
        {
            var committed = new List<Recording> { At("debris", 10.0, debris: true) };
            Assert.Equal(-1, RecordingsTableUI.FindAggregateMainRecordingIndex(new[] { 0 }, committed));
            Assert.Equal(-1, RecordingsTableUI.FindAggregateMainRecordingIndex(new int[0], committed));
            Assert.Equal(-1, RecordingsTableUI.FindAggregateMainRecordingIndex(null, committed));
            Assert.Equal(-1, RecordingsTableUI.FindAggregateMainRecordingIndex(new[] { 0 }, null));
        }

        // ---------- CompareRootItemsForSort -------------------------------------

        private static int Compare(string aName, double aKey, string bName, double bKey,
            RecordingsTableUI.SortColumn column, bool ascending)
        {
            return RecordingsTableUI.CompareRootItemsForSort(
                false, null, aName, aKey, true, "group", bName, bKey, column, ascending);
        }

        [Fact]
        public void CompareRootItemsForSort_TextColumnsCompareNamesIgnoringCase()
        {
            foreach (var column in new[]
            {
                RecordingsTableUI.SortColumn.Name,
                RecordingsTableUI.SortColumn.Phase,
                RecordingsTableUI.SortColumn.LaunchSite,
            })
            {
                Assert.True(Compare("alpha", 9.0, "Bravo", 1.0, column, true) < 0, column.ToString());
                Assert.True(Compare("alpha", 9.0, "Bravo", 1.0, column, false) > 0, column.ToString());
                Assert.Equal(0, Compare("ALPHA", 9.0, "alpha", 1.0, column, true));
            }
        }

        [Fact]
        public void CompareRootItemsForSort_OtherColumnsCompareSortKeys()
        {
            foreach (var column in new[]
            {
                RecordingsTableUI.SortColumn.Index,
                RecordingsTableUI.SortColumn.LaunchTime,
                RecordingsTableUI.SortColumn.Duration,
                RecordingsTableUI.SortColumn.Status,
            })
            {
                Assert.True(Compare("zulu", 1.0, "alpha", 9.0, column, true) < 0, column.ToString());
                Assert.True(Compare("zulu", 1.0, "alpha", 9.0, column, false) > 0, column.ToString());
                Assert.Equal(0, Compare("zulu", 5.0, "alpha", 5.0, column, false));
            }
        }

        // ---------- ResolveParentVesselName --------------------------------------

        [Fact]
        public void ResolveParentVesselName_NotEvaOrNoParent_Null()
        {
            var committed = new List<Recording> { new Recording { RecordingId = "p", VesselName = "Ship" } };
            Assert.Null(RecordingsTableUI.ResolveParentVesselName(
                new Recording { ParentRecordingId = "p" }, committed));
            Assert.Null(RecordingsTableUI.ResolveParentVesselName(
                new Recording { EvaCrewName = "Jeb" }, committed));
        }

        [Fact]
        public void ResolveParentVesselName_CommittedListThenCommittedTrees()
        {
            var committed = new List<Recording> { new Recording { RecordingId = "p", VesselName = "Ship" } };
            Assert.Equal("Ship", RecordingsTableUI.ResolveParentVesselName(
                new Recording { EvaCrewName = "Jeb", ParentRecordingId = "p" }, committed));

            var tree = new RecordingTree { Id = "t", TreeName = "t" };
            tree.AddOrReplaceRecording(new Recording { RecordingId = "tree_parent", VesselName = "Station" });
            RecordingStore.AddCommittedTreeForTesting(tree);

            Assert.Equal("Station", RecordingsTableUI.ResolveParentVesselName(
                new Recording { EvaCrewName = "Bill", ParentRecordingId = "tree_parent" }, committed));
            Assert.Null(RecordingsTableUI.ResolveParentVesselName(
                new Recording { EvaCrewName = "Bob", ParentRecordingId = "nowhere" }, committed));
        }

        // ---------- GetRecordingVisualStatusText / Tooltip ----------------------

        [Fact]
        public void GetRecordingVisualStatus_TextAndTooltipPerKind()
        {
            Assert.Equal("static", RecordingsTableUI.GetRecordingVisualStatusText(
                RecordingVisualKind.StaticPlaceholder));
            Assert.Equal("stationary", RecordingsTableUI.GetRecordingVisualStatusText(
                RecordingVisualKind.StationaryTail));
            Assert.Null(RecordingsTableUI.GetRecordingVisualStatusText(RecordingVisualKind.Normal));

            Assert.StartsWith("Static placeholder:", RecordingsTableUI.GetRecordingVisualStatusTooltip(
                RecordingVisualKind.StaticPlaceholder));
            Assert.StartsWith("Stationary tail:", RecordingsTableUI.GetRecordingVisualStatusTooltip(
                RecordingVisualKind.StationaryTail));
            Assert.Null(RecordingsTableUI.GetRecordingVisualStatusTooltip(RecordingVisualKind.Normal));
        }
    }
}
