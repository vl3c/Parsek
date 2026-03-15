using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for methods extracted from ParsekUI during Tier 2B refactor:
    /// BuildSortedActionEvents and BuildGroupTreeData.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekUIExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekUIExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region BuildSortedActionEvents

        [Fact]
        public void BuildSortedActionEvents_EmptyMilestones_ReturnsEmptyList()
        {
            var milestones = new List<Milestone>();
            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 0,
                ParsekUI.ActionsSortColumn.Time, true);
            Assert.Empty(result);
        }

        [Fact]
        public void BuildSortedActionEvents_SkipsUncommittedMilestones()
        {
            var milestones = new List<Milestone>
            {
                new Milestone
                {
                    Committed = false,
                    Epoch = 1,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent { ut = 100, eventType = GameStateEventType.CrewHired, key = "Jeb" }
                    }
                }
            };

            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 1,
                ParsekUI.ActionsSortColumn.Time, true);
            Assert.Empty(result);
        }

        [Fact]
        public void BuildSortedActionEvents_SkipsDifferentEpoch()
        {
            var milestones = new List<Milestone>
            {
                new Milestone
                {
                    Committed = true,
                    Epoch = 2,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent { ut = 100, eventType = GameStateEventType.CrewHired, key = "Jeb" }
                    }
                }
            };

            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 1,
                ParsekUI.ActionsSortColumn.Time, true);
            Assert.Empty(result);
        }

        [Fact]
        public void BuildSortedActionEvents_IncludesCommittedEventsOfCurrentEpoch()
        {
            var milestones = new List<Milestone>
            {
                new Milestone
                {
                    Committed = true,
                    Epoch = 1,
                    LastReplayedEventIndex = -1,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent { ut = 100, eventType = GameStateEventType.CrewHired, key = "Jeb" },
                        new GameStateEvent { ut = 200, eventType = GameStateEventType.TechResearched, key = "node1" }
                    }
                }
            };

            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 1,
                ParsekUI.ActionsSortColumn.Time, true);
            Assert.Equal(2, result.Count);
            Assert.Equal(100, result[0].Item1.ut);
            Assert.Equal(200, result[1].Item1.ut);
        }

        [Fact]
        public void BuildSortedActionEvents_SortByTimeDescending()
        {
            var milestones = new List<Milestone>
            {
                new Milestone
                {
                    Committed = true,
                    Epoch = 0,
                    LastReplayedEventIndex = -1,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent { ut = 100, eventType = GameStateEventType.CrewHired, key = "Jeb" },
                        new GameStateEvent { ut = 300, eventType = GameStateEventType.TechResearched, key = "node1" },
                        new GameStateEvent { ut = 200, eventType = GameStateEventType.PartPurchased, key = "mk1" }
                    }
                }
            };

            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 0,
                ParsekUI.ActionsSortColumn.Time, false);
            Assert.Equal(3, result.Count);
            Assert.Equal(300, result[0].Item1.ut);
            Assert.Equal(200, result[1].Item1.ut);
            Assert.Equal(100, result[2].Item1.ut);
        }

        [Fact]
        public void BuildSortedActionEvents_MarksReplayedCorrectly()
        {
            var milestones = new List<Milestone>
            {
                new Milestone
                {
                    Committed = true,
                    Epoch = 0,
                    LastReplayedEventIndex = 0, // first event replayed
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent { ut = 100, eventType = GameStateEventType.CrewHired, key = "Jeb" },
                        new GameStateEvent { ut = 200, eventType = GameStateEventType.TechResearched, key = "node1" }
                    }
                }
            };

            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 0,
                ParsekUI.ActionsSortColumn.Time, true);
            Assert.Equal(2, result.Count);
            Assert.True(result[0].Item2);   // index 0 <= LastReplayedEventIndex(0) -> replayed
            Assert.False(result[1].Item2);  // index 1 > LastReplayedEventIndex(0) -> pending
        }

        [Fact]
        public void BuildSortedActionEvents_SortByStatusSeparatesPendingFromReplayed()
        {
            var milestones = new List<Milestone>
            {
                new Milestone
                {
                    Committed = true,
                    Epoch = 0,
                    LastReplayedEventIndex = 0,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent { ut = 100, eventType = GameStateEventType.CrewHired, key = "Jeb" },
                        new GameStateEvent { ut = 200, eventType = GameStateEventType.TechResearched, key = "node1" }
                    }
                }
            };

            var result = ParsekUI.BuildSortedActionEvents(
                milestones, 0,
                ParsekUI.ActionsSortColumn.Status, true);
            Assert.Equal(2, result.Count);
            // ascending: false (Pending) < true (Replayed)
            Assert.False(result[0].Item2);  // pending first
            Assert.True(result[1].Item2);   // replayed second
        }

        #endregion

        #region BuildGroupTreeData

        private static RecordingStore.Recording MakeRec(string name, string chainId = null,
            List<string> groups = null)
        {
            return new RecordingStore.Recording
            {
                VesselName = name,
                ChainId = chainId,
                RecordingGroups = groups
            };
        }

        [Fact]
        public void BuildGroupTreeData_EmptyInput_ProducesEmptyOutput()
        {
            var committed = new List<RecordingStore.Recording>();
            int[] sorted = new int[0];
            var emptyGroups = new List<string>();
            var groupParents = new Dictionary<string, string>();

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Empty(grpToRecs);
            Assert.Empty(chainToRecs);
            Assert.Empty(grpChildren);
            Assert.Empty(rootGrps);
            Assert.Empty(rootChainIds);
        }

        [Fact]
        public void BuildGroupTreeData_RecordingsInGroups_MapsCorrectly()
        {
            var committed = new List<RecordingStore.Recording>
            {
                MakeRec("Alpha", groups: new List<string> { "GroupA" }),
                MakeRec("Beta", groups: new List<string> { "GroupA", "GroupB" }),
                MakeRec("Gamma") // no groups
            };
            int[] sorted = new int[] { 0, 1, 2 };
            var emptyGroups = new List<string>();
            var groupParents = new Dictionary<string, string>();

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Equal(2, grpToRecs.Count);
            Assert.Contains(0, grpToRecs["GroupA"]);
            Assert.Contains(1, grpToRecs["GroupA"]);
            Assert.Contains(1, grpToRecs["GroupB"]);
            Assert.DoesNotContain("GroupC", grpToRecs.Keys);
        }

        [Fact]
        public void BuildGroupTreeData_ChainRecordingsGrouped()
        {
            var committed = new List<RecordingStore.Recording>
            {
                MakeRec("Seg1", chainId: "chain1"),
                MakeRec("Seg2", chainId: "chain1"),
                MakeRec("Solo")
            };
            int[] sorted = new int[] { 0, 1, 2 };
            var emptyGroups = new List<string>();
            var groupParents = new Dictionary<string, string>();

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Single(chainToRecs);
            Assert.Equal(2, chainToRecs["chain1"].Count);
            Assert.Contains("chain1", rootChainIds);
        }

        [Fact]
        public void BuildGroupTreeData_ChainInGroup_NotInRootChains()
        {
            var committed = new List<RecordingStore.Recording>
            {
                MakeRec("Seg1", chainId: "chain1", groups: new List<string> { "GroupA" }),
                MakeRec("Seg2", chainId: "chain1", groups: new List<string> { "GroupA" })
            };
            int[] sorted = new int[] { 0, 1 };
            var emptyGroups = new List<string>();
            var groupParents = new Dictionary<string, string>();

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.DoesNotContain("chain1", rootChainIds);
        }

        [Fact]
        public void BuildGroupTreeData_GroupHierarchy_BuildsChildrenAndRoots()
        {
            var committed = new List<RecordingStore.Recording>
            {
                MakeRec("Alpha", groups: new List<string> { "Sub" })
            };
            int[] sorted = new int[] { 0 };
            var emptyGroups = new List<string>();
            var groupParents = new Dictionary<string, string>
            {
                { "Sub", "Parent" }
            };

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Contains("Parent", rootGrps);
            Assert.DoesNotContain("Sub", rootGrps);
            Assert.True(grpChildren.ContainsKey("Parent"));
            Assert.Contains("Sub", grpChildren["Parent"]);
        }

        [Fact]
        public void BuildGroupTreeData_EmptyGroupsIncluded()
        {
            var committed = new List<RecordingStore.Recording>();
            int[] sorted = new int[0];
            var emptyGroups = new List<string> { "EmptyOne" };
            var groupParents = new Dictionary<string, string>();

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            Assert.Contains("EmptyOne", rootGrps);
        }

        [Fact]
        public void BuildGroupTreeData_SortedIndicesRespected()
        {
            // If sorted indices present recording 2 before 0, the order in grpToRecs
            // should reflect that (recording 2 appears first in the list)
            var committed = new List<RecordingStore.Recording>
            {
                MakeRec("Alpha", groups: new List<string> { "G" }),
                MakeRec("Beta"),
                MakeRec("Gamma", groups: new List<string> { "G" })
            };
            int[] sorted = new int[] { 2, 1, 0 }; // reverse order
            var emptyGroups = new List<string>();
            var groupParents = new Dictionary<string, string>();

            ParsekUI.BuildGroupTreeData(sorted, committed, emptyGroups, groupParents,
                out var grpToRecs, out var chainToRecs, out var grpChildren,
                out var rootGrps, out var rootChainIds);

            // Both 0 and 2 should be in group G, with 2 first (sorted order)
            Assert.Equal(2, grpToRecs["G"].Count);
            Assert.Equal(2, grpToRecs["G"][0]);
            Assert.Equal(0, grpToRecs["G"][1]);
        }

        #endregion
    }
}
