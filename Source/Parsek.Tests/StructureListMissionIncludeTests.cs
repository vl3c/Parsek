using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The mission Log follows the MISSION, not its tree (inventory finding P10: every clone
    /// of a mission read the same Log). Against the committed bdock-recorded fixture: the
    /// docking mission "Kerbal X #2" and a clone of it that drops a vessel.
    /// </summary>
    [Collection("Sequential")]
    public class StructureListMissionIncludeTests : IDisposable
    {
        private const string DockingTreeId = "8c677bba0ec04e558c6ebec4ad8c3c9f";
        private const string ProbeHeadId = "500c0ba9";

        private readonly List<string> logLines = new List<string>();

        public StructureListMissionIncludeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            DockEventGraph.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionStructureBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
        }

        private static RecordingTree LoadDockingTree()
            => StructureListBdockFixtureTests.LoadBdockTrees().Single(t => t.Id == DockingTreeId);

        private static List<StructureStep> Log(RecordingTree tree, Mission mission)
            => MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree),
                StructureListBdockFixtureTests.StockTitles, null,
                mission != null ? mission.ExcludedIntervalKeys : null);

        // The vessel row the Missions tab draws for a head id prefix, from the same
        // composition the window builds.
        private static MissionVesselRow FindRow(RecordingTree tree, string headPrefix)
        {
            List<MissionVesselRow> rows = MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree)));
            var stack = new Stack<MissionVesselRow>(rows);
            while (stack.Count > 0)
            {
                MissionVesselRow r = stack.Pop();
                if (r.OwnerHeadId.StartsWith(headPrefix, StringComparison.Ordinal)) return r;
                foreach (MissionVesselRow c in r.Children) stack.Push(c);
            }
            return null;
        }

        // catches P10: a clone that excludes a vessel drew the same Log as its original.
        // The clone's own include toggle is the Missions vessel-row toggle, applied to the
        // clone; the probe's End row is the one row only that vessel owns.
        [Fact]
        public void Clone_ExcludingAVessel_ShowsFewerRows_OriginalUnchanged()
        {
            RecordingTree tree = LoadDockingTree();
            var original = new Mission("orig", DockingTreeId, "Kerbal X #2");
            Mission clone = original.Clone("copy");
            MissionVesselRow probe = FindRow(tree, ProbeHeadId);
            Assert.NotNull(probe);
            Assert.True(MissionVesselRowBuilder.ApplyVesselInclusion(probe, false, clone.ExcludedIntervalKeys) > 0);
            Assert.Equal(MissionVesselInclusion.None,
                MissionVesselRowBuilder.ClassifyInclusion(probe, clone.ExcludedIntervalKeys));

            List<StructureStep> originalLog = Log(tree, original);
            List<StructureStep> cloneLog = Log(tree, clone);

            Assert.Equal(11, originalLog.Count);
            Assert.Empty(original.ExcludedIntervalKeys);
            Assert.Equal(10, cloneLog.Count);
            Assert.Contains(originalLog, s => s.Kind == StructureStepKind.Terminal
                && s.RecordingId.StartsWith(ProbeHeadId, StringComparison.Ordinal));
            Assert.DoesNotContain(cloneLog, s => s.RecordingId != null
                && s.RecordingId.StartsWith(ProbeHeadId, StringComparison.Ordinal));
            // The parent's own separation row stays: it is the parent's event, inside the
            // parent's included interval.
            Assert.Contains(cloneLog, s => s.Kind == StructureStepKind.Separation
                && s.Label.StartsWith("Decoupled", StringComparison.Ordinal));
        }

        // catches: a start-trimmed mission (the launch interval excluded) still listing the
        // launch and the staging that happened inside the dropped interval.
        [Fact]
        public void StartTrim_DropsTheLaunchIntervalRows_KeepsTheBoundaryRow()
        {
            RecordingTree tree = LoadDockingTree();
            var mission = new Mission("trim", DockingTreeId, "Kerbal X #2 trim");
            MissionVesselRow root = FindRow(tree, "5157d655");
            string launchKey = root.Intervals[0].HeadLegId;
            mission.ExcludedIntervalKeys.Add(launchKey);

            List<StructureStep> log = Log(tree, mission);

            Assert.DoesNotContain(log, s => s.Kind == StructureStepKind.Launch);
            Assert.DoesNotContain(log, s => s.Label.StartsWith("Staged", StringComparison.Ordinal));
            // The probe separation closes the excluded interval and opens the kept one.
            Assert.Contains(log, s => s.Label == "Decoupled (Kerbal X Probe)");
            Assert.Contains(log, s => s.Kind == StructureStepKind.Dock);
        }

        // catches: the filter reading a key the vessel rows do not (a forked predicate). A key
        // naming nothing in the tree changes nothing.
        [Fact]
        public void UnknownKey_KeepsEveryRow()
        {
            RecordingTree tree = LoadDockingTree();
            var mission = new Mission("m", DockingTreeId, "m");
            mission.ExcludedIntervalKeys.Add("not-an-interval");
            Assert.Equal(11, Log(tree, mission).Count);
        }

        [Fact]
        public void DropExcludedSteps_LogsTheDroppedCount()
        {
            RecordingTree tree = LoadDockingTree();
            var mission = new Mission("m", DockingTreeId, "m");
            MissionVesselRowBuilder.ApplyVesselInclusion(
                FindRow(tree, ProbeHeadId), false, mission.ExcludedIntervalKeys);
            Log(tree, mission);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("BuildStructureList: tree=" + DockingTreeId)
                && l.Contains("excludedKeys=1 excludedRows=1"));
        }

        // catches: the open Log not following an include toggle (it rebuilt only on open).
        [Fact]
        public void ChangeSignature_MovesOnIncludeToggle_NameAndRecordings()
        {
            RecordingTree tree = LoadDockingTree();
            var mission = new Mission("m", DockingTreeId, "Kerbal X #2");
            int before = StructureListWindowUI.ComputeChangeSignature(
                7, true, mission.Name, mission.ExcludedIntervalKeys);

            MissionVesselRow probe = FindRow(tree, ProbeHeadId);
            MissionVesselRowBuilder.ApplyVesselInclusion(probe, false, mission.ExcludedIntervalKeys);
            int excluded = StructureListWindowUI.ComputeChangeSignature(
                7, true, mission.Name, mission.ExcludedIntervalKeys);
            Assert.NotEqual(before, excluded);

            MissionVesselRowBuilder.ApplyVesselInclusion(probe, true, mission.ExcludedIntervalKeys);
            Assert.Equal(before, StructureListWindowUI.ComputeChangeSignature(
                7, true, mission.Name, mission.ExcludedIntervalKeys));

            Assert.NotEqual(before, StructureListWindowUI.ComputeChangeSignature(
                8, true, mission.Name, mission.ExcludedIntervalKeys));
            Assert.NotEqual(before, StructureListWindowUI.ComputeChangeSignature(
                7, true, "Renamed", mission.ExcludedIntervalKeys));
            Assert.NotEqual(before, StructureListWindowUI.ComputeChangeSignature(
                7, false, mission.Name, mission.ExcludedIntervalKeys));
        }

        [Fact]
        public void ChangeSignature_IgnoresSetOrder()
        {
            var a = new List<string> { "x", "y/seg1", "z@dock1" };
            var b = new List<string> { "z@dock1", "x", "y/seg1" };
            Assert.Equal(
                StructureListWindowUI.ComputeChangeSignature(1, true, "n", a),
                StructureListWindowUI.ComputeChangeSignature(1, true, "n", b));
            Assert.NotEqual(
                StructureListWindowUI.ComputeChangeSignature(1, true, "n", a),
                StructureListWindowUI.ComputeChangeSignature(1, true, "n", new List<string> { "x", "y/seg1" }));
        }

        // The one include predicate: the render windows, the vessel rows and the Log agree.
        [Fact]
        public void IsIntervalIncluded_IsTheVesselRowPredicate()
        {
            var node = new MissionCompositionNode { HeadLegId = "k" };
            Assert.True(MissionIntervalSelection.IsIntervalIncluded(node, null));
            Assert.True(MissionIntervalSelection.IsIntervalIncluded(node, new HashSet<string>()));
            Assert.True(MissionIntervalSelection.IsIntervalIncluded(node, new HashSet<string> { "other" }));
            Assert.False(MissionIntervalSelection.IsIntervalIncluded(node, new HashSet<string> { "k" }));
        }
    }
}
