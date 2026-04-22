using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure Group Picker hierarchy and selection rules extracted from IMGUI code.
    /// </summary>
    public class GroupPickerPresentationTests
    {
        [Fact]
        public void NormalizeRecordingSelection_RemovesInvalidAndDuplicates_PreservesFirstOrder()
        {
            List<int> normalized = GroupPickerPresentation.NormalizeRecordingSelection(
                new List<int> { 3, -1, 1, 3, 7, 2 },
                committedCount: 4);

            Assert.Equal(new List<int> { 3, 1, 2 }, normalized);
        }

        [Fact]
        public void GetCommonGroups_ReturnsIntersectionAcrossSelection()
        {
            var committed = new List<Recording>
            {
                new Recording
                {
                    RecordingGroups = new List<string> { "Crew", "Shared", "Mun" }
                },
                new Recording
                {
                    RecordingGroups = new List<string> { "Shared", "Science", "Mun" }
                },
                new Recording
                {
                    RecordingGroups = new List<string> { "Shared" }
                }
            };

            HashSet<string> common = GroupPickerPresentation.GetCommonGroups(
                new List<int> { 0, 1 },
                committed,
                new List<string> { "Crew", "Shared", "Science", "Mun" });

            Assert.True(common.SetEquals(new[] { "Shared", "Mun" }));
        }

        [Fact]
        public void BuildExpandedGroups_IncludesParentsKnownEmptyAndExistingRecordingGroups()
        {
            HashSet<string> expanded = GroupPickerPresentation.BuildExpandedGroups(
                new Dictionary<string, string>
                {
                    ["Child"] = "Root"
                },
                new List<string> { "Flights" },
                new List<string> { "Empty" });

            Assert.True(expanded.SetEquals(new[] { "Root", "Flights", "Empty" }));
        }

        [Fact]
        public void BuildTreeModel_SortsRootsChildrenAndMarksSelectedGroupDescendantsInvalid()
        {
            GroupPickerTreeModel model = GroupPickerPresentation.BuildTreeModel(
                new List<string> { "zeta", "beta" },
                new Dictionary<string, string>
                {
                    ["Mun"] = "Flights",
                    ["Minmus"] = "Flights",
                    ["Lab"] = "Mun"
                },
                new List<string> { "empty" },
                selectedGroup: "Mun");

            Assert.Equal(new List<string> { "beta", "empty", "Flights", "zeta" }, model.RootNames);
            Assert.Equal(new List<string> { "Minmus", "Mun" }, model.ParentToChildren["Flights"]);
            Assert.Equal(new List<string> { "Lab" }, model.ParentToChildren["Mun"]);
            Assert.True(model.CycleInvalid.SetEquals(new[] { "Mun", "Lab" }));
        }

        [Fact]
        public void ApplySelectionToggle_SingleSelect_ReplacesExistingSelection()
        {
            HashSet<string> updated = GroupPickerPresentation.ApplySelectionToggle(
                new HashSet<string> { "Crew", "Science" },
                "Flights",
                isChecked: true,
                singleSelect: true);

            Assert.True(updated.SetEquals(new[] { "Flights" }));
        }

        [Fact]
        public void ApplySelectionToggle_MultiSelect_UncheckedItemIsRemoved()
        {
            HashSet<string> updated = GroupPickerPresentation.ApplySelectionToggle(
                new HashSet<string> { "Crew", "Science" },
                "Crew",
                isChecked: false,
                singleSelect: false);

            Assert.True(updated.SetEquals(new[] { "Science" }));
        }

        [Fact]
        public void TryCreateGroupName_TrimmedUniqueName_IsAccepted()
        {
            bool ok = GroupPickerPresentation.TryCreateGroupName(
                "  New Group  ",
                new List<string> { "Flights" },
                new List<string> { "Empty" },
                out string newName);

            Assert.True(ok);
            Assert.Equal("New Group", newName);
        }

        [Theory]
        [InlineData("Flights")]
        [InlineData("Empty")]
        [InlineData("bad=name")]
        [InlineData("")]
        public void TryCreateGroupName_ExistingOrInvalidName_IsRejected(string rawName)
        {
            bool ok = GroupPickerPresentation.TryCreateGroupName(
                rawName,
                new List<string> { "Flights" },
                new List<string> { "Empty" },
                out string newName);

            Assert.False(ok);
            Assert.Equal(rawName != null ? rawName.Trim() : null, newName);
        }

        [Fact]
        public void ComputeMembershipDelta_ReturnsAddedAndRemovedSets()
        {
            GroupMembershipDelta delta = GroupPickerPresentation.ComputeMembershipDelta(
                new HashSet<string> { "Crew", "Flights" },
                new HashSet<string> { "Flights", "Science" });

            Assert.True(delta.Added.SetEquals(new[] { "Science" }));
            Assert.True(delta.Removed.SetEquals(new[] { "Crew" }));
        }
    }
}
