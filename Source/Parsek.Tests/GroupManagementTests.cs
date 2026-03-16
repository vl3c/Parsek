using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GroupManagementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GroupManagementTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekScenario.ResetGroupsForTesting();
            ParsekScenario.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetGroupsForTesting();
            ParsekScenario.ResetReplacementsForTesting();
        }

        // ─── IsInAncestorChain ──────────────────────────────────────────────

        [Fact]
        public void IsInAncestorChain_DirectParent_ReturnsTrue()
        {
            ParsekScenario.groupParents["Child"] = "Parent";

            Assert.True(ParsekScenario.IsInAncestorChain("Child", "Parent"));
        }

        [Fact]
        public void IsInAncestorChain_Grandparent_ReturnsTrue()
        {
            ParsekScenario.groupParents["C"] = "B";
            ParsekScenario.groupParents["B"] = "A";

            Assert.True(ParsekScenario.IsInAncestorChain("C", "A"));
        }

        [Fact]
        public void IsInAncestorChain_Self_ReturnsTrue()
        {
            Assert.True(ParsekScenario.IsInAncestorChain("X", "X"));
        }

        [Fact]
        public void IsInAncestorChain_Unrelated_ReturnsFalse()
        {
            ParsekScenario.groupParents["A"] = "Root";
            ParsekScenario.groupParents["B"] = "Root";

            Assert.False(ParsekScenario.IsInAncestorChain("A", "B"));
        }

        [Fact]
        public void IsInAncestorChain_EmptyGroupParents_ReturnsFalse()
        {
            // groupParents is empty after ResetGroupsForTesting
            Assert.False(ParsekScenario.IsInAncestorChain("Alpha", "Beta"));
        }

        [Fact]
        public void IsInAncestorChain_MaxDepthGuard_DoesNotHang()
        {
            // Build a chain of 150 groups — exceeds the 100-depth guard
            for (int i = 0; i < 150; i++)
                ParsekScenario.groupParents[$"G{i + 1}"] = $"G{i}";

            // Should hit max depth and return true (assumes cycle)
            bool result = ParsekScenario.IsInAncestorChain("G150", "G0");
            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("max depth reached"));
        }

        [Fact]
        public void IsInAncestorChain_NullGroup_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsInAncestorChain(null, "A"));
        }

        [Fact]
        public void IsInAncestorChain_NullCandidate_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsInAncestorChain("A", null));
        }

        // ─── SetGroupParent ────────────────────────────────────────────────

        [Fact]
        public void SetGroupParent_NormalAssignment_ReturnsTrue()
        {
            bool result = ParsekScenario.SetGroupParent("Child", "Parent");

            Assert.True(result);
            Assert.True(ParsekScenario.groupParents.ContainsKey("Child"));
            Assert.Contains(logLines, l => l.Contains("assigned to parent group"));
        }

        [Fact]
        public void SetGroupParent_NullParent_RemovesFromHierarchy()
        {
            ParsekScenario.groupParents["Child"] = "Parent";

            bool result = ParsekScenario.SetGroupParent("Child", null);

            Assert.True(result);
            Assert.False(ParsekScenario.groupParents.ContainsKey("Child"));
            Assert.Contains(logLines, l => l.Contains("moved to root level"));
        }

        [Fact]
        public void SetGroupParent_CycleDetection_ReturnsFalse()
        {
            ParsekScenario.SetGroupParent("B", "A");

            bool result = ParsekScenario.SetGroupParent("A", "B");

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("would create cycle"));
        }

        [Fact]
        public void SetGroupParent_SelfAssignment_ReturnsFalse()
        {
            bool result = ParsekScenario.SetGroupParent("X", "X");

            Assert.False(result);
        }

        [Fact]
        public void SetGroupParent_EmptyChild_ReturnsFalse()
        {
            bool result = ParsekScenario.SetGroupParent("", "Parent");

            Assert.False(result);
        }

        // ─── RemoveGroupFromHierarchy ──────────────────────────────────────

        [Fact]
        public void RemoveGroupFromHierarchy_ReparentsChildrenToGrandparent()
        {
            ParsekScenario.groupParents["Child1"] = "Middle";
            ParsekScenario.groupParents["Child2"] = "Middle";
            ParsekScenario.groupParents["Middle"] = "Root";

            ParsekScenario.RemoveGroupFromHierarchy("Middle");

            // Middle should be gone
            Assert.False(ParsekScenario.groupParents.ContainsKey("Middle"));
            // Children reparented to grandparent "Root"
            Assert.Equal("Root", ParsekScenario.groupParents["Child1"]);
            Assert.Equal("Root", ParsekScenario.groupParents["Child2"]);
        }

        [Fact]
        public void RemoveGroupFromHierarchy_RootGroup_PromotesChildrenToRoot()
        {
            ParsekScenario.groupParents["Sub1"] = "Parent";
            ParsekScenario.groupParents["Sub2"] = "Parent";
            ParsekScenario.groupParents["Sub3"] = "Parent";

            ParsekScenario.RemoveGroupFromHierarchy("Parent");

            // Parent was root-level (no grandparent), so children become root
            Assert.False(ParsekScenario.groupParents.ContainsKey("Sub1"));
            Assert.False(ParsekScenario.groupParents.ContainsKey("Sub2"));
            Assert.False(ParsekScenario.groupParents.ContainsKey("Sub3"));
            Assert.Contains(logLines, l =>
                l.Contains("removed from hierarchy") && l.Contains("3 sub-groups promoted to root"));
        }

        [Fact]
        public void RemoveGroupFromHierarchy_NoChildren_LogsZero()
        {
            ParsekScenario.groupParents["Leaf"] = "Root";

            ParsekScenario.RemoveGroupFromHierarchy("Leaf");

            Assert.Contains(logLines, l =>
                l.Contains("removed from hierarchy") && l.Contains("0 sub-groups"));
        }

        // ─── GetDescendantGroups ───────────────────────────────────────────

        [Fact]
        public void GetDescendantGroups_SingleLevel_ReturnsChildren()
        {
            ParsekScenario.groupParents["Child1"] = "Root";
            ParsekScenario.groupParents["Child2"] = "Root";

            var descendants = ParsekScenario.GetDescendantGroups("Root");

            Assert.True(descendants.Count == 2);
            Assert.Contains("Child1", descendants);
            Assert.Contains("Child2", descendants);
        }

        [Fact]
        public void GetDescendantGroups_MultiLevel_ReturnsGrandchildren()
        {
            ParsekScenario.groupParents["B"] = "A";
            ParsekScenario.groupParents["C"] = "B";

            var descendants = ParsekScenario.GetDescendantGroups("A");

            Assert.True(descendants.Count == 2);
            Assert.Contains("B", descendants);
            Assert.Contains("C", descendants);
        }

        [Fact]
        public void GetDescendantGroups_NoDescendants_ReturnsEmptyList()
        {
            ParsekScenario.groupParents["Leaf"] = "Root";

            var descendants = ParsekScenario.GetDescendantGroups("Leaf");

            Assert.True(descendants.Count == 0);
        }

        [Fact]
        public void GetDescendantGroups_NullGroup_ReturnsEmptyList()
        {
            var descendants = ParsekScenario.GetDescendantGroups(null);

            Assert.True(descendants.Count == 0);
        }

        // ─── RenameGroupInHierarchy ────────────────────────────────────────

        [Fact]
        public void RenameGroupInHierarchy_UpdatesAsChild()
        {
            ParsekScenario.groupParents["OldName"] = "Parent";

            ParsekScenario.RenameGroupInHierarchy("OldName", "NewName");

            Assert.False(ParsekScenario.groupParents.ContainsKey("OldName"));
            Assert.True(ParsekScenario.groupParents.ContainsKey("NewName"));
            Assert.Contains(logLines, l => l.Contains("RenameGroupInHierarchy"));
        }

        [Fact]
        public void RenameGroupInHierarchy_UpdatesAsParent()
        {
            ParsekScenario.groupParents["Child"] = "OldParent";

            ParsekScenario.RenameGroupInHierarchy("OldParent", "NewParent");

            Assert.True(ParsekScenario.groupParents.ContainsKey("Child"));
            string parentVal;
            ParsekScenario.groupParents.TryGetValue("Child", out parentVal);
            Assert.Equal("NewParent", parentVal);
        }

        [Fact]
        public void RenameGroupInHierarchy_BothKeyAndValue()
        {
            // "Mid" is both a child of "Top" and a parent of "Bottom"
            ParsekScenario.groupParents["Mid"] = "Top";
            ParsekScenario.groupParents["Bottom"] = "Mid";

            ParsekScenario.RenameGroupInHierarchy("Mid", "Middle");

            Assert.False(ParsekScenario.groupParents.ContainsKey("Mid"));
            Assert.True(ParsekScenario.groupParents.ContainsKey("Middle"));
            string topParent;
            ParsekScenario.groupParents.TryGetValue("Middle", out topParent);
            Assert.Equal("Top", topParent);
            string bottomParent;
            ParsekScenario.groupParents.TryGetValue("Bottom", out bottomParent);
            Assert.Equal("Middle", bottomParent);
        }

        [Fact]
        public void RenameGroupInHierarchy_SameName_NoOp()
        {
            ParsekScenario.groupParents["X"] = "Root";

            ParsekScenario.RenameGroupInHierarchy("X", "X");

            // No log emitted since it's a no-op
            Assert.DoesNotContain(logLines, l => l.Contains("RenameGroupInHierarchy"));
        }

        // ─── RecordingStore.AddRecordingToGroup / RemoveRecordingFromGroup ─

        [Fact]
        public void AddRecordingToGroup_CreatesListIfNull()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "TestVessel",
                RecordingGroups = null
            });

            RecordingStore.AddRecordingToGroup(0, "MyGroup");

            var rec = RecordingStore.CommittedRecordings[0];
            Assert.NotNull(rec.RecordingGroups);
            Assert.Contains("MyGroup", rec.RecordingGroups);
            Assert.Contains(logLines, l => l.Contains("added to group"));
        }

        [Fact]
        public void AddRecordingToGroup_Idempotent_NoDuplicates()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "TestVessel",
                RecordingGroups = null
            });

            RecordingStore.AddRecordingToGroup(0, "MyGroup");
            RecordingStore.AddRecordingToGroup(0, "MyGroup");

            var rec = RecordingStore.CommittedRecordings[0];
            Assert.True(rec.RecordingGroups.Count == 1);
        }

        [Fact]
        public void RemoveRecordingFromGroup_CleansUpEmptyListToNull()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "TestVessel",
                RecordingGroups = new List<string> { "OnlyGroup" }
            });

            RecordingStore.RemoveRecordingFromGroup(0, "OnlyGroup");

            var rec = RecordingStore.CommittedRecordings[0];
            Assert.Null(rec.RecordingGroups);
            Assert.Contains(logLines, l => l.Contains("removed from group"));
        }

        [Fact]
        public void RemoveRecordingFromGroup_Idempotent_NoErrorIfNotMember()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "TestVessel",
                RecordingGroups = new List<string> { "GroupA" }
            });

            // Removing a group the recording doesn't belong to — should be a no-op
            RecordingStore.RemoveRecordingFromGroup(0, "NonExistent");

            var rec = RecordingStore.CommittedRecordings[0];
            Assert.NotNull(rec.RecordingGroups);
            Assert.True(rec.RecordingGroups.Count == 1);
            Assert.Contains("GroupA", rec.RecordingGroups);
        }

        [Fact]
        public void RemoveRecordingFromGroup_NullGroups_NoError()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "TestVessel",
                RecordingGroups = null
            });

            // Should not throw
            RecordingStore.RemoveRecordingFromGroup(0, "Anything");

            Assert.Null(RecordingStore.CommittedRecordings[0].RecordingGroups);
        }

        // ─── RecordingStore.AddChainToGroup / RemoveChainFromGroup ─────────

        [Fact]
        public void AddChainToGroup_AddsToAllChainMembers()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Seg0", ChainId = "chain-1", RecordingGroups = null
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Seg1", ChainId = "chain-1", RecordingGroups = null
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Other", ChainId = null, RecordingGroups = null
            });

            RecordingStore.AddChainToGroup("chain-1", "Flights");

            Assert.Contains("Flights", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains("Flights", RecordingStore.CommittedRecordings[1].RecordingGroups);
            Assert.Null(RecordingStore.CommittedRecordings[2].RecordingGroups);
            Assert.Contains(logLines, l => l.Contains("2 members added to group"));
        }

        [Fact]
        public void AddChainToGroup_OnlyAffectsMatchingChain()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "A0", ChainId = "chain-a", RecordingGroups = null
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "B0", ChainId = "chain-b", RecordingGroups = null
            });

            RecordingStore.AddChainToGroup("chain-a", "GroupX");

            Assert.Contains("GroupX", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Null(RecordingStore.CommittedRecordings[1].RecordingGroups);
        }

        [Fact]
        public void RemoveChainFromGroup_RemovesFromAllChainMembers()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Seg0", ChainId = "chain-1",
                RecordingGroups = new List<string> { "Flights" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Seg1", ChainId = "chain-1",
                RecordingGroups = new List<string> { "Flights" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Other", ChainId = null,
                RecordingGroups = new List<string> { "Flights" }
            });

            RecordingStore.RemoveChainFromGroup("chain-1", "Flights");

            // Chain members should have group removed (and list set to null since empty)
            Assert.Null(RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Null(RecordingStore.CommittedRecordings[1].RecordingGroups);
            // Non-chain member should keep its group
            Assert.Contains("Flights", RecordingStore.CommittedRecordings[2].RecordingGroups);
            Assert.Contains(logLines, l => l.Contains("2 members removed from group"));
        }

        [Fact]
        public void RemoveChainFromGroup_OnlyAffectsMatchingChain()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "A0", ChainId = "chain-a",
                RecordingGroups = new List<string> { "SharedGroup" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "B0", ChainId = "chain-b",
                RecordingGroups = new List<string> { "SharedGroup" }
            });

            RecordingStore.RemoveChainFromGroup("chain-a", "SharedGroup");

            Assert.Null(RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains("SharedGroup", RecordingStore.CommittedRecordings[1].RecordingGroups);
        }

        // ─── RecordingStore.RenameGroup ────────────────────────────────────

        [Fact]
        public void RenameGroup_RenamesInAllRecordingGroupLists()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Vessel1",
                RecordingGroups = new List<string> { "OldName" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Vessel2",
                RecordingGroups = new List<string> { "OldName" }
            });

            bool result = RecordingStore.RenameGroup("OldName", "NewName");

            Assert.True(result);
            Assert.Contains("NewName", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains("NewName", RecordingStore.CommittedRecordings[1].RecordingGroups);
            Assert.DoesNotContain("OldName", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains(logLines, l =>
                l.Contains("RenameGroup") && l.Contains("2 recordings updated"));
        }

        [Fact]
        public void RenameGroup_ReturnsFalseOnCollision()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Vessel1",
                RecordingGroups = new List<string> { "GroupA" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Vessel2",
                RecordingGroups = new List<string> { "GroupB" }
            });

            bool result = RecordingStore.RenameGroup("GroupA", "GroupB");

            Assert.False(result);
            // Original names should be unchanged
            Assert.Contains("GroupA", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains("GroupB", RecordingStore.CommittedRecordings[1].RecordingGroups);
            Assert.Contains(logLines, l => l.Contains("name already exists"));
        }

        [Fact]
        public void RenameGroup_HandlesMultipleGroups()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Multi",
                RecordingGroups = new List<string> { "Alpha", "Beta", "Gamma" }
            });

            bool result = RecordingStore.RenameGroup("Beta", "BetaRenamed");

            Assert.True(result);
            var groups = RecordingStore.CommittedRecordings[0].RecordingGroups;
            Assert.Contains("Alpha", groups);
            Assert.Contains("BetaRenamed", groups);
            Assert.Contains("Gamma", groups);
            Assert.DoesNotContain("Beta", groups);
        }

        [Fact]
        public void RenameGroup_SameName_ReturnsFalse()
        {
            bool result = RecordingStore.RenameGroup("Same", "Same");

            Assert.False(result);
        }

        // ─── RecordingStore.GetGroupNames ──────────────────────────────────

        [Fact]
        public void GetGroupNames_ReturnsDistinctSortedNames()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Zebra", "Alpha" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V2",
                RecordingGroups = new List<string> { "Alpha", "Middle" }
            });

            var names = RecordingStore.GetGroupNames();

            Assert.True(names.Count == 3);
            Assert.Equal("Alpha", names[0]);
            Assert.Equal("Middle", names[1]);
            Assert.Equal("Zebra", names[2]);
        }

        [Fact]
        public void GetGroupNames_EmptyRecordings_ReturnsEmptyList()
        {
            var names = RecordingStore.GetGroupNames();

            Assert.True(names.Count == 0);
        }

        [Fact]
        public void GetGroupNames_AllNullGroups_ReturnsEmptyList()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = null
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V2",
                RecordingGroups = null
            });

            var names = RecordingStore.GetGroupNames();

            Assert.True(names.Count == 0);
        }

        // ─── RecordingStore.RemoveGroupFromAll ─────────────────────────────

        [Fact]
        public void RemoveGroupFromAll_RemovesFromAllRecordings_ReturnsCount()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Target", "Keep" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V2",
                RecordingGroups = new List<string> { "Target" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V3",
                RecordingGroups = new List<string> { "Other" }
            });

            int count = RecordingStore.RemoveGroupFromAll("Target");

            Assert.Equal(2, count);
            // V1 should still have "Keep"
            Assert.Contains("Keep", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.DoesNotContain("Target", RecordingStore.CommittedRecordings[0].RecordingGroups);
            // V2 had only "Target" so RecordingGroups should be null
            Assert.Null(RecordingStore.CommittedRecordings[1].RecordingGroups);
            // V3 should be unaffected
            Assert.Contains("Other", RecordingStore.CommittedRecordings[2].RecordingGroups);
            Assert.Contains(logLines, l =>
                l.Contains("RemoveGroupFromAll") && l.Contains("removed from 2 recordings"));
        }

        [Fact]
        public void RemoveGroupFromAll_NoMatches_ReturnsZero()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "GroupA" }
            });

            int count = RecordingStore.RemoveGroupFromAll("NonExistent");

            Assert.Equal(0, count);
            // Original group should be untouched
            Assert.Contains("GroupA", RecordingStore.CommittedRecordings[0].RecordingGroups);
        }

        [Fact]
        public void RemoveGroupFromAll_NullGroupName_ReturnsZero()
        {
            int count = RecordingStore.RemoveGroupFromAll(null);

            Assert.Equal(0, count);
        }

        // ─── RecordingStore.ReplaceGroupOnAll ───────────────────────────────

        [Fact]
        public void ReplaceGroupOnAll_ReplacesWithParentGroup()
        {
            // Bug: recording stays tagged with disbanded child group instead of being moved to parent
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "OldGroup", "Keep" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V2",
                RecordingGroups = new List<string> { "OldGroup" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("OldGroup", "ParentGroup");

            Assert.Equal(2, count);
            Assert.Contains("ParentGroup", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains("Keep", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.DoesNotContain("OldGroup", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains("ParentGroup", RecordingStore.CommittedRecordings[1].RecordingGroups);
            Assert.DoesNotContain("OldGroup", RecordingStore.CommittedRecordings[1].RecordingGroups);
            Assert.Contains(logLines, l =>
                l.Contains("ReplaceGroupOnAll") && l.Contains("2 recordings"));
        }

        [Fact]
        public void ReplaceGroupOnAll_AlreadyHasParentGroup_Deduplicates()
        {
            // Bug: recording ends up with duplicate parent group tag if it was already a member
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "OldGroup", "ParentGroup" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("OldGroup", "ParentGroup");

            Assert.Equal(1, count);
            var groups = RecordingStore.CommittedRecordings[0].RecordingGroups;
            Assert.Single(groups);
            Assert.Equal("ParentGroup", groups[0]);
        }

        [Fact]
        public void ReplaceGroupOnAll_NullParent_RemovesGroupTag()
        {
            // Bug: recording retains group tag when group is disbanded to standalone (null parent)
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Disband" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("Disband", null);

            Assert.Equal(1, count);
            // List was emptied so should be nulled out
            Assert.Null(RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains(logLines, l =>
                l.Contains("ReplaceGroupOnAll") && l.Contains("(standalone)"));
        }

        [Fact]
        public void ReplaceGroupOnAll_NullRecordingGroups_SkipsWithoutCrash()
        {
            // Bug: NullReferenceException when recording has null RecordingGroups list
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "NullGroups",
                RecordingGroups = null
            });

            int count = RecordingStore.ReplaceGroupOnAll("AnyGroup", "Parent");

            Assert.Equal(0, count);
            Assert.Null(RecordingStore.CommittedRecordings[0].RecordingGroups);
        }

        [Fact]
        public void ReplaceGroupOnAll_RecordingNotInGroup_Unaffected()
        {
            // Bug: unrelated recordings get their groups modified by ReplaceGroupOnAll
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "Bystander",
                RecordingGroups = new List<string> { "SomeOtherGroup" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("TargetGroup", "Parent");

            Assert.Equal(0, count);
            Assert.Contains("SomeOtherGroup", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.True(RecordingStore.CommittedRecordings[0].RecordingGroups.Count == 1);
        }

        [Fact]
        public void ReplaceGroupOnAll_ReturnValueMatchesUpdatedCount()
        {
            // Bug: return value doesn't match actual number of modified recordings
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Target" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V2",
                RecordingGroups = new List<string> { "Other" }
            });
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V3",
                RecordingGroups = new List<string> { "Target", "Extra" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("Target", "Replacement");

            Assert.Equal(2, count);
        }

        [Fact]
        public void ReplaceGroupOnAll_EmptyGroupName_ReturnsZero()
        {
            // Bug: empty string group name could match empty entries in RecordingGroups
            RecordingStore.CommittedRecordings.Add(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Group" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("", "Parent");

            Assert.Equal(0, count);
        }

        // ─── hiddenGroups persistence ───────────────────────────────────────

        [Fact]
        public void HiddenGroups_OnSave_WritesHiddenGroupsNode()
        {
            // Bug: hidden groups not persisted — all groups reappear after save/load
            ParsekScenario.hiddenGroups.Add("Archived");
            ParsekScenario.hiddenGroups.Add("Deprecated");

            // Simulate what OnSave does for HIDDEN_GROUPS
            var node = new ConfigNode("SCENARIO");
            if (ParsekScenario.hiddenGroups.Count > 0)
            {
                ConfigNode hiddenNode = node.AddNode("HIDDEN_GROUPS");
                foreach (var g in ParsekScenario.hiddenGroups)
                    hiddenNode.AddValue("group", g);
            }

            ConfigNode savedNode = node.GetNode("HIDDEN_GROUPS");
            Assert.NotNull(savedNode);
            string[] groups = savedNode.GetValues("group");
            Assert.Equal(2, groups.Length);
            Assert.Contains("Archived", groups);
            Assert.Contains("Deprecated", groups);
        }

        [Fact]
        public void HiddenGroups_LoadHiddenGroups_ReadsBack()
        {
            // Bug: LoadHiddenGroups fails to populate hiddenGroups from ConfigNode
            var node = new ConfigNode("SCENARIO");
            var hiddenNode = node.AddNode("HIDDEN_GROUPS");
            hiddenNode.AddValue("group", "HiddenA");
            hiddenNode.AddValue("group", "HiddenB");

            ParsekScenario.LoadHiddenGroups(node);

            Assert.Equal(2, ParsekScenario.hiddenGroups.Count);
            Assert.Contains("HiddenA", ParsekScenario.hiddenGroups);
            Assert.Contains("HiddenB", ParsekScenario.hiddenGroups);
        }

        [Fact]
        public void HiddenGroups_LoadHiddenGroups_NoNode_ClearsSet()
        {
            // Bug: stale hidden groups survive when save file has no HIDDEN_GROUPS node
            ParsekScenario.hiddenGroups.Add("Stale");

            var node = new ConfigNode("SCENARIO");
            // No HIDDEN_GROUPS node

            ParsekScenario.LoadHiddenGroups(node);

            Assert.Empty(ParsekScenario.hiddenGroups);
        }

        [Fact]
        public void HiddenGroups_RoundTrip_SaveThenLoad()
        {
            // Bug: hidden groups lost across save/load cycle (end-to-end)
            ParsekScenario.hiddenGroups.Add("Flights");
            ParsekScenario.hiddenGroups.Add("Tests");

            // Save
            var node = new ConfigNode("SCENARIO");
            if (ParsekScenario.hiddenGroups.Count > 0)
            {
                ConfigNode hiddenNode = node.AddNode("HIDDEN_GROUPS");
                foreach (var g in ParsekScenario.hiddenGroups)
                    hiddenNode.AddValue("group", g);
            }

            // Clear and reload
            ParsekScenario.hiddenGroups.Clear();
            Assert.Empty(ParsekScenario.hiddenGroups);

            ParsekScenario.LoadHiddenGroups(node);

            Assert.Equal(2, ParsekScenario.hiddenGroups.Count);
            Assert.Contains("Flights", ParsekScenario.hiddenGroups);
            Assert.Contains("Tests", ParsekScenario.hiddenGroups);
        }

        [Fact]
        public void HiddenGroups_LoadHiddenGroups_SkipsEmptyStrings()
        {
            // Bug: empty string entry in HIDDEN_GROUPS node pollutes the set
            var node = new ConfigNode("SCENARIO");
            var hiddenNode = node.AddNode("HIDDEN_GROUPS");
            hiddenNode.AddValue("group", "Valid");
            hiddenNode.AddValue("group", "");
            hiddenNode.AddValue("group", "AlsoValid");

            ParsekScenario.LoadHiddenGroups(node);

            Assert.Equal(2, ParsekScenario.hiddenGroups.Count);
            Assert.Contains("Valid", ParsekScenario.hiddenGroups);
            Assert.Contains("AlsoValid", ParsekScenario.hiddenGroups);
            Assert.DoesNotContain("", ParsekScenario.hiddenGroups);
        }
    }
}
