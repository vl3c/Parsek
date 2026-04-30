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
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekScenario.SetInstanceForTesting(null);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekScenario.SetInstanceForTesting(null);
        }

        private static Recording MakeTreeRecording(string id, string name, string treeId,
            bool isDebris = false, string evaCrewName = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                TreeId = treeId,
                IsDebris = isDebris,
                EvaCrewName = evaCrewName,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 200.0 }
                }
            };
        }

        // ─── IsInAncestorChain ──────────────────────────────────────────────

        [Fact]
        public void IsInAncestorChain_DirectParent_ReturnsTrue()
        {
            GroupHierarchyStore.groupParents["Child"] = "Parent";

            Assert.True(GroupHierarchyStore.IsInAncestorChain("Child", "Parent"));
        }

        [Fact]
        public void IsInAncestorChain_Grandparent_ReturnsTrue()
        {
            GroupHierarchyStore.groupParents["C"] = "B";
            GroupHierarchyStore.groupParents["B"] = "A";

            Assert.True(GroupHierarchyStore.IsInAncestorChain("C", "A"));
        }

        [Fact]
        public void IsInAncestorChain_Self_ReturnsTrue()
        {
            Assert.True(GroupHierarchyStore.IsInAncestorChain("X", "X"));
        }

        [Fact]
        public void IsInAncestorChain_Unrelated_ReturnsFalse()
        {
            GroupHierarchyStore.groupParents["A"] = "Root";
            GroupHierarchyStore.groupParents["B"] = "Root";

            Assert.False(GroupHierarchyStore.IsInAncestorChain("A", "B"));
        }

        [Fact]
        public void IsInAncestorChain_EmptyGroupParents_ReturnsFalse()
        {
            // groupParents is empty after ResetGroupsForTesting
            Assert.False(GroupHierarchyStore.IsInAncestorChain("Alpha", "Beta"));
        }

        [Fact]
        public void IsInAncestorChain_MaxDepthGuard_DoesNotHang()
        {
            // Build a chain of 150 groups — exceeds the 100-depth guard
            for (int i = 0; i < 150; i++)
                GroupHierarchyStore.groupParents[$"G{i + 1}"] = $"G{i}";

            // Should hit max depth and return true (assumes cycle)
            bool result = GroupHierarchyStore.IsInAncestorChain("G150", "G0");
            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("max depth reached"));
        }

        [Fact]
        public void IsInAncestorChain_NullGroup_ReturnsFalse()
        {
            Assert.False(GroupHierarchyStore.IsInAncestorChain(null, "A"));
        }

        [Fact]
        public void IsInAncestorChain_NullCandidate_ReturnsFalse()
        {
            Assert.False(GroupHierarchyStore.IsInAncestorChain("A", null));
        }

        // ─── SetGroupParent ────────────────────────────────────────────────

        [Fact]
        public void SetGroupParent_NormalAssignment_ReturnsTrue()
        {
            bool result = GroupHierarchyStore.SetGroupParent("Child", "Parent");

            Assert.True(result);
            Assert.True(GroupHierarchyStore.groupParents.ContainsKey("Child"));
            Assert.Contains(logLines, l => l.Contains("assigned to parent group"));
        }

        [Fact]
        public void SetGroupParent_NullParent_RemovesFromHierarchy()
        {
            GroupHierarchyStore.groupParents["Child"] = "Parent";

            bool result = GroupHierarchyStore.SetGroupParent("Child", null);

            Assert.True(result);
            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("Child"));
            Assert.Contains(logLines, l => l.Contains("moved to root level"));
        }

        [Fact]
        public void SetGroupParent_CycleDetection_ReturnsFalse()
        {
            GroupHierarchyStore.SetGroupParent("B", "A");

            bool result = GroupHierarchyStore.SetGroupParent("A", "B");

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("would create cycle"));
        }

        [Fact]
        public void SetGroupParent_SelfAssignment_ReturnsFalse()
        {
            bool result = GroupHierarchyStore.SetGroupParent("X", "X");

            Assert.False(result);
        }

        [Fact]
        public void SetGroupParent_EmptyChild_ReturnsFalse()
        {
            bool result = GroupHierarchyStore.SetGroupParent("", "Parent");

            Assert.False(result);
        }

        [Fact]
        public void SetGroupParent_PermanentRootGroup_CannotBeNested()
        {
            bool result = GroupHierarchyStore.SetGroupParent(RecordingStore.GloopsGroupName, "Parent");

            Assert.False(result);
            Assert.False(GroupHierarchyStore.groupParents.ContainsKey(RecordingStore.GloopsGroupName));
            Assert.Contains(logLines, l => l.Contains("cannot assign permanent root group"));
        }

        // ─── RemoveGroupFromHierarchy ──────────────────────────────────────

        [Fact]
        public void RemoveGroupFromHierarchy_ReparentsChildrenToGrandparent()
        {
            GroupHierarchyStore.groupParents["Child1"] = "Middle";
            GroupHierarchyStore.groupParents["Child2"] = "Middle";
            GroupHierarchyStore.groupParents["Middle"] = "Root";

            GroupHierarchyStore.RemoveGroupFromHierarchy("Middle");

            // Middle should be gone
            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("Middle"));
            // Children reparented to grandparent "Root"
            Assert.Equal("Root", GroupHierarchyStore.groupParents["Child1"]);
            Assert.Equal("Root", GroupHierarchyStore.groupParents["Child2"]);
        }

        [Fact]
        public void RemoveGroupFromHierarchy_RootGroup_PromotesChildrenToRoot()
        {
            GroupHierarchyStore.groupParents["Sub1"] = "Parent";
            GroupHierarchyStore.groupParents["Sub2"] = "Parent";
            GroupHierarchyStore.groupParents["Sub3"] = "Parent";

            GroupHierarchyStore.RemoveGroupFromHierarchy("Parent");

            // Parent was root-level (no grandparent), so children become root
            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("Sub1"));
            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("Sub2"));
            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("Sub3"));
            Assert.Contains(logLines, l =>
                l.Contains("removed from hierarchy") && l.Contains("3 sub-groups promoted to root"));
        }

        [Fact]
        public void RemoveGroupFromHierarchy_NoChildren_LogsZero()
        {
            GroupHierarchyStore.groupParents["Leaf"] = "Root";

            GroupHierarchyStore.RemoveGroupFromHierarchy("Leaf");

            Assert.Contains(logLines, l =>
                l.Contains("removed from hierarchy") && l.Contains("0 sub-groups"));
        }

        [Fact]
        public void PruneUnusedHierarchyEntries_KeepsLiveAncestorsAndRemovesStaleAutoGroups()
        {
            GroupHierarchyStore.groupParents["Kerbal X / Debris"] = "Kerbal X";
            GroupHierarchyStore.groupParents["Kerbal X (2) / Debris"] = "Kerbal X (2)";
            GroupHierarchyStore.hiddenGroups.Add("Kerbal X (2) / Debris");

            var liveMain = new Recording
            {
                RecordingId = "rec-live-main",
                VesselName = "Kerbal X",
                RecordingGroups = new List<string> { "Kerbal X" }
            };
            var liveDebris = new Recording
            {
                RecordingId = "rec-live-debris",
                VesselName = "Kerbal X Debris",
                RecordingGroups = new List<string> { "Kerbal X / Debris" }
            };
            var oldSuperseded = new Recording
            {
                RecordingId = "rec-old-superseded",
                VesselName = "Old Booster",
                RecordingGroups = new List<string> { "Kerbal X (2) / Debris" }
            };
            var newSuperseding = new Recording
            {
                RecordingId = "rec-new-superseding",
                VesselName = "New Booster",
                RecordingGroups = new List<string> { "Kerbal X" }
            };
            RecordingStore.AddRecordingWithTreeForTesting(liveMain);
            RecordingStore.AddRecordingWithTreeForTesting(liveDebris);
            RecordingStore.AddRecordingWithTreeForTesting(oldSuperseded);
            RecordingStore.AddRecordingWithTreeForTesting(newSuperseding);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_groups",
                        OldRecordingId = oldSuperseded.RecordingId,
                        NewRecordingId = newSuperseding.RecordingId
                    }
                }
            });

            int removed = GroupHierarchyStore.PruneUnusedHierarchyEntriesFromCommittedRecordings("test");

            Assert.Equal(2, removed);
            Assert.True(GroupHierarchyStore.TryGetGroupParent("Kerbal X / Debris", out var parent));
            Assert.Equal("Kerbal X", parent);
            Assert.False(GroupHierarchyStore.HasGroupParent("Kerbal X (2) / Debris"));
            Assert.DoesNotContain("Kerbal X (2) / Debris", GroupHierarchyStore.HiddenGroups);
            Assert.Contains(logLines, l =>
                l.Contains("[GroupHierarchy]")
                && l.Contains("Pruned stale group hierarchy")
                && l.Contains("reason=test"));
        }

        [Fact]
        public void ClearCommitted_PrunesGroupHierarchy()
        {
            GroupHierarchyStore.groupParents["Stale / Debris"] = "Stale";
            GroupHierarchyStore.hiddenGroups.Add("Stale / Debris");
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = "rec-group-clear",
                VesselName = "Group Clear",
                RecordingGroups = new List<string> { "Stale / Debris" }
            });

            RecordingStore.ClearCommitted();

            Assert.Empty(GroupHierarchyStore.GroupParents);
            Assert.Empty(GroupHierarchyStore.HiddenGroups);
        }

        // ─── GetDescendantGroups ───────────────────────────────────────────

        [Fact]
        public void GetDescendantGroups_SingleLevel_ReturnsChildren()
        {
            GroupHierarchyStore.groupParents["Child1"] = "Root";
            GroupHierarchyStore.groupParents["Child2"] = "Root";

            var descendants = GroupHierarchyStore.GetDescendantGroups("Root");

            Assert.True(descendants.Count == 2);
            Assert.Contains("Child1", descendants);
            Assert.Contains("Child2", descendants);
        }

        [Fact]
        public void GetDescendantGroups_MultiLevel_ReturnsGrandchildren()
        {
            GroupHierarchyStore.groupParents["B"] = "A";
            GroupHierarchyStore.groupParents["C"] = "B";

            var descendants = GroupHierarchyStore.GetDescendantGroups("A");

            Assert.True(descendants.Count == 2);
            Assert.Contains("B", descendants);
            Assert.Contains("C", descendants);
        }

        [Fact]
        public void GetDescendantGroups_NoDescendants_ReturnsEmptyList()
        {
            GroupHierarchyStore.groupParents["Leaf"] = "Root";

            var descendants = GroupHierarchyStore.GetDescendantGroups("Leaf");

            Assert.True(descendants.Count == 0);
        }

        [Fact]
        public void GetDescendantGroups_NullGroup_ReturnsEmptyList()
        {
            var descendants = GroupHierarchyStore.GetDescendantGroups(null);

            Assert.True(descendants.Count == 0);
        }

        // ─── RenameGroupInHierarchy ────────────────────────────────────────

        [Fact]
        public void RenameGroupInHierarchy_UpdatesAsChild()
        {
            GroupHierarchyStore.groupParents["OldName"] = "Parent";

            GroupHierarchyStore.RenameGroupInHierarchy("OldName", "NewName");

            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("OldName"));
            Assert.True(GroupHierarchyStore.groupParents.ContainsKey("NewName"));
            Assert.Contains(logLines, l => l.Contains("RenameGroupInHierarchy"));
        }

        [Fact]
        public void RenameGroupInHierarchy_UpdatesAsParent()
        {
            GroupHierarchyStore.groupParents["Child"] = "OldParent";

            GroupHierarchyStore.RenameGroupInHierarchy("OldParent", "NewParent");

            Assert.True(GroupHierarchyStore.groupParents.ContainsKey("Child"));
            string parentVal;
            GroupHierarchyStore.groupParents.TryGetValue("Child", out parentVal);
            Assert.Equal("NewParent", parentVal);
        }

        [Fact]
        public void RenameGroupInHierarchy_BothKeyAndValue()
        {
            // "Mid" is both a child of "Top" and a parent of "Bottom"
            GroupHierarchyStore.groupParents["Mid"] = "Top";
            GroupHierarchyStore.groupParents["Bottom"] = "Mid";

            GroupHierarchyStore.RenameGroupInHierarchy("Mid", "Middle");

            Assert.False(GroupHierarchyStore.groupParents.ContainsKey("Mid"));
            Assert.True(GroupHierarchyStore.groupParents.ContainsKey("Middle"));
            string topParent;
            GroupHierarchyStore.groupParents.TryGetValue("Middle", out topParent);
            Assert.Equal("Top", topParent);
            string bottomParent;
            GroupHierarchyStore.groupParents.TryGetValue("Bottom", out bottomParent);
            Assert.Equal("Middle", bottomParent);
        }

        [Fact]
        public void RenameGroupInHierarchy_SameName_NoOp()
        {
            GroupHierarchyStore.groupParents["X"] = "Root";

            GroupHierarchyStore.RenameGroupInHierarchy("X", "X");

            // No log emitted since it's a no-op
            Assert.DoesNotContain(logLines, l => l.Contains("RenameGroupInHierarchy"));
        }

        // ─── RecordingStore.AddRecordingToGroup / RemoveRecordingFromGroup ─

        [Fact]
        public void AddRecordingToGroup_CreatesListIfNull()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Seg0", ChainId = "chain-1", RecordingGroups = null
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Seg1", ChainId = "chain-1", RecordingGroups = null
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "A0", ChainId = "chain-a", RecordingGroups = null
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Seg0", ChainId = "chain-1",
                RecordingGroups = new List<string> { "Flights" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Seg1", ChainId = "chain-1",
                RecordingGroups = new List<string> { "Flights" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "A0", ChainId = "chain-a",
                RecordingGroups = new List<string> { "SharedGroup" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Vessel1",
                RecordingGroups = new List<string> { "OldName" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Vessel1",
                RecordingGroups = new List<string> { "GroupA" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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

        [Fact]
        public void RenameGroup_GloopsPermanentRootGroup_ReturnsFalse()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "Ghost",
                RecordingGroups = new List<string> { RecordingStore.GloopsGroupName }
            });

            bool result = RecordingStore.RenameGroup(RecordingStore.GloopsGroupName, "Showcase Ghosts");

            Assert.False(result);
            Assert.Contains(RecordingStore.GloopsGroupName, RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.DoesNotContain("Showcase Ghosts", RecordingStore.CommittedRecordings[0].RecordingGroups);
            Assert.Contains(logLines, l => l.Contains("cannot rename permanent root group"));
        }

        [Fact]
        public void IsAutoGeneratedTreeGroup_RenamedMissionGroupsRemainProtected()
        {
            var root = MakeTreeRecording("root", "Mission", "tree-mission");
            var crew = MakeTreeRecording("crew", "Jeb", "tree-mission", evaCrewName: "Jeb");
            crew.ParentRecordingId = root.RecordingId;

            var tree = new RecordingTree
            {
                Id = "tree-mission",
                TreeName = "Mission",
                RootRecordingId = root.RecordingId,
                Recordings = new Dictionary<string, Recording>
                {
                    [root.RecordingId] = root,
                    [crew.RecordingId] = crew
                }
            };

            RecordingStore.CommitTree(tree);

            string rootGroup = root.RecordingGroups[0];
            string crewGroup = crew.RecordingGroups[0];

            Assert.True(RecordingStore.RenameGroup(rootGroup, "Flight Alpha"));
            GroupHierarchyStore.RenameGroupInHierarchy(rootGroup, "Flight Alpha");

            Assert.True(RecordingStore.RenameGroup(crewGroup, "Crew Notes"));
            GroupHierarchyStore.RenameGroupInHierarchy(crewGroup, "Crew Notes");

            Assert.True(RecordingStore.IsAutoGeneratedTreeGroup("Flight Alpha"));
            Assert.True(RecordingStore.IsAutoGeneratedTreeGroup("Crew Notes"));
            Assert.False(RecordingStore.IsAutoGeneratedTreeGroup(rootGroup));
            Assert.False(RecordingStore.IsAutoGeneratedTreeGroup(crewGroup));
        }

        [Fact]
        public void IsAutoGeneratedTreeGroup_CustomParentFolderIsNotProtected()
        {
            var root = MakeTreeRecording("root", "Mission", "tree-folder");
            var child = MakeTreeRecording("child", "Mission Copy", "tree-folder");

            var tree = new RecordingTree
            {
                Id = "tree-folder",
                TreeName = "Mission",
                RootRecordingId = root.RecordingId,
                Recordings = new Dictionary<string, Recording>
                {
                    [root.RecordingId] = root,
                    [child.RecordingId] = child
                }
            };

            RecordingStore.CommitTree(tree);

            string rootGroup = root.RecordingGroups[0];
            Assert.True(GroupHierarchyStore.SetGroupParent(rootGroup, "Folder"));

            Assert.True(RecordingStore.IsAutoGeneratedTreeGroup(rootGroup));
            Assert.False(RecordingStore.IsAutoGeneratedTreeGroup("Folder"));
        }

        [Fact]
        public void IsAutoGeneratedTreeGroup_RecordingLevelEditsDoNotChangeProtectedGroup()
        {
            var root = MakeTreeRecording("root", "Mission", "tree-edits");
            var child = MakeTreeRecording("child", "Mission Copy", "tree-edits");

            var tree = new RecordingTree
            {
                Id = "tree-edits",
                TreeName = "Mission",
                RootRecordingId = root.RecordingId,
                Recordings = new Dictionary<string, Recording>
                {
                    [root.RecordingId] = root,
                    [child.RecordingId] = child
                }
            };

            RecordingStore.CommitTree(tree);

            string rootGroup = root.RecordingGroups[0];
            var committedRoot = RecordingStore.CommittedRecordings[0];
            committedRoot.RecordingGroups = new List<string> { "Manual Group" };

            Assert.True(RecordingStore.IsAutoGeneratedTreeGroup(rootGroup));
            Assert.False(RecordingStore.IsAutoGeneratedTreeGroup("Manual Group"));
        }

        [Fact]
        public void IsAutoGeneratedTreeGroup_CustomCrewOrDebrisNamedGroupsStayDisbandable()
        {
            var root = MakeTreeRecording("root", "Mission", "tree-custom");
            var sibling = MakeTreeRecording("sibling", "Mission B", "tree-custom");

            var tree = new RecordingTree
            {
                Id = "tree-custom",
                TreeName = "Mission",
                RootRecordingId = root.RecordingId,
                Recordings = new Dictionary<string, Recording>
                {
                    [root.RecordingId] = root,
                    [sibling.RecordingId] = sibling
                }
            };

            RecordingStore.CommitTree(tree);

            string rootGroup = root.RecordingGroups[0];
            string customCrewGroup = rootGroup + " / Crew";
            string customDebrisGroup = rootGroup + " / Debris";

            RecordingStore.AddRecordingToGroup(0, customCrewGroup);
            RecordingStore.AddRecordingToGroup(1, customDebrisGroup);
            Assert.True(GroupHierarchyStore.SetGroupParent(customCrewGroup, rootGroup));
            Assert.True(GroupHierarchyStore.SetGroupParent(customDebrisGroup, rootGroup));

            Assert.False(RecordingStore.IsAutoGeneratedTreeGroup(customCrewGroup));
            Assert.False(RecordingStore.IsAutoGeneratedTreeGroup(customDebrisGroup));
        }

        [Fact]
        public void IsPermanentGroup_ReturnsTrueForGloopsAndAutoGeneratedTreeGroups()
        {
            var root = MakeTreeRecording("root", "Mission", "tree-permanent");
            var crew = MakeTreeRecording("crew", "Jeb", "tree-permanent", evaCrewName: "Jeb");
            crew.ParentRecordingId = root.RecordingId;

            var tree = new RecordingTree
            {
                Id = "tree-permanent",
                TreeName = "Mission",
                RootRecordingId = root.RecordingId,
                Recordings = new Dictionary<string, Recording>
                {
                    [root.RecordingId] = root,
                    [crew.RecordingId] = crew
                }
            };

            RecordingStore.CommitTree(tree);

            Assert.True(RecordingStore.IsPermanentGroup(RecordingStore.GloopsGroupName));
            Assert.True(RecordingStore.IsPermanentGroup(root.RecordingGroups[0]));
            Assert.True(RecordingStore.IsPermanentGroup(crew.RecordingGroups[0]));
            Assert.False(RecordingStore.IsPermanentGroup("Manual Group"));
        }

        // ─── RecordingStore.GetGroupNames ──────────────────────────────────

        [Fact]
        public void GetGroupNames_ReturnsDistinctSortedNames()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Zebra", "Alpha" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V1",
                RecordingGroups = null
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Target", "Keep" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V2",
                RecordingGroups = new List<string> { "Target" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "OldGroup", "Keep" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Target" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V2",
                RecordingGroups = new List<string> { "Other" }
            });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                VesselName = "V1",
                RecordingGroups = new List<string> { "Group" }
            });

            int count = RecordingStore.ReplaceGroupOnAll("", "Parent");

            Assert.Equal(0, count);
        }

        // ─── hiddenGroups persistence ───────────────────────────────────────

        [Fact]
        public void HiddenGroups_LoadHiddenGroups_ReadsBack()
        {
            // Bug: LoadHiddenGroups fails to populate hiddenGroups from ConfigNode
            var node = new ConfigNode("SCENARIO");
            var hiddenNode = node.AddNode("HIDDEN_GROUPS");
            hiddenNode.AddValue("group", "HiddenA");
            hiddenNode.AddValue("group", "HiddenB");

            GroupHierarchyStore.LoadHiddenGroups(node);

            Assert.Equal(2, GroupHierarchyStore.hiddenGroups.Count);
            Assert.Contains("HiddenA", GroupHierarchyStore.hiddenGroups);
            Assert.Contains("HiddenB", GroupHierarchyStore.hiddenGroups);
        }

        [Fact]
        public void HiddenGroups_LoadHiddenGroups_NoNode_ClearsSet()
        {
            // Bug: stale hidden groups survive when save file has no HIDDEN_GROUPS node
            GroupHierarchyStore.hiddenGroups.Add("Stale");

            var node = new ConfigNode("SCENARIO");
            // No HIDDEN_GROUPS node

            GroupHierarchyStore.LoadHiddenGroups(node);

            Assert.Empty(GroupHierarchyStore.hiddenGroups);
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

            GroupHierarchyStore.LoadHiddenGroups(node);

            Assert.Equal(2, GroupHierarchyStore.hiddenGroups.Count);
            Assert.Contains("Valid", GroupHierarchyStore.hiddenGroups);
            Assert.Contains("AlsoValid", GroupHierarchyStore.hiddenGroups);
            Assert.DoesNotContain("", GroupHierarchyStore.hiddenGroups);
        }
    }
}
