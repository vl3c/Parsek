using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A tree whose EVA kerbal placed ground parts (Breaking Ground science, deployables)
    /// created vessels, so the idle-on-pad scene-exit auto-discard must not throw it away
    /// even when every recording stayed within 30 m of its start (a science cluster set
    /// up beside a landed capsule). An EVA with no placement stays idle.
    /// </summary>
    [Collection("Sequential")]
    public class PlacedPartsNotIdleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PlacedPartsNotIdleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording IdleRec(string id, string name)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = name,
                MaxDistanceFromLaunch = 12.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Mun" });
            return rec;
        }

        // Capsule + EVA kerbal, both idle, joined by an EVA branch point.
        private static RecordingTree LanderWithEvaTree()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Mun Lander",
                Recordings = new Dictionary<string, Recording>()
            };
            tree.Recordings["capsule"] = IdleRec("capsule", "Mun Lander");
            tree.Recordings["kerbal"] = IdleRec("kerbal", "Jebediah Kerman");
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-eva",
                UT = 120.0,
                Type = BranchPointType.EVA,
                ParentRecordingIds = new List<string> { "capsule" },
                ChildRecordingIds = new List<string> { "kerbal" }
            });
            return tree;
        }

        private static void AddPlacement(RecordingTree tree, string partName, uint pid)
        {
            var (bp, member) = GroundPartPlacement.BuildPlacementBranchData(
                "kerbal", tree.Id, 150.0, pid, partName, 1);
            member.MaxDistanceFromLaunch = 0.0;
            member.Points.Add(new TrajectoryPoint { ut = 150.0, bodyName = "Mun" });
            member.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Mun" });
            tree.BranchPoints.Add(bp);
            tree.Recordings[member.RecordingId] = member;
        }

        [Fact]
        public void IsTreeIdleOnPad_EvaWithoutPlacement_StaysIdle()
        {
            RecordingTree tree = LanderWithEvaTree();

            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.DoesNotContain(logLines, l => l.Contains("placed ground part(s)"));
        }

        [Fact]
        public void IsTreeIdleOnPad_PlacedGroundParts_NotIdle()
        {
            RecordingTree tree = LanderWithEvaTree();
            AddPlacement(tree, "Deployed Seismic Sensor", 5001u);
            AddPlacement(tree, "Deployed Solar Panel", 5002u);
            AddPlacement(tree, "Deployed Comms", 5003u);
            AddPlacement(tree, "Deployed Science Station", 5004u);

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("IsTreeIdleOnPad: not idle - tree has 4 placed ground part(s)")
                && l.Contains("tree='Mun Lander'"));
        }

        [Fact]
        public void IsTreeIdleOnPad_SinglePlacement_NotIdle()
        {
            RecordingTree tree = LanderWithEvaTree();
            AddPlacement(tree, "Deployed Seismic Sensor", 5001u);

            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
            Assert.Contains(logLines, l =>
                l.Contains("IsTreeIdleOnPad: not idle - tree has 1 placed ground part(s)"));
        }

        [Fact]
        public void CountPlacedGroundParts_CountsOnlyPlacementBranchPoints()
        {
            RecordingTree tree = LanderWithEvaTree();
            Assert.Equal(0, ParsekFlight.CountPlacedGroundParts(tree));

            AddPlacement(tree, "Deployed Seismic Sensor", 5001u);
            AddPlacement(tree, "Deployed Solar Panel", 5002u);
            tree.BranchPoints.Add(null);
            Assert.Equal(2, ParsekFlight.CountPlacedGroundParts(tree));
        }

        [Fact]
        public void CountPlacedGroundParts_NullSafe()
        {
            Assert.Equal(0, ParsekFlight.CountPlacedGroundParts(null));
            Assert.Equal(0, ParsekFlight.CountPlacedGroundParts(
                new RecordingTree { BranchPoints = null }));
        }

        [Fact]
        public void TreeHasPlacedGroundPartsForIdle_NamesTheCaller()
        {
            RecordingTree tree = LanderWithEvaTree();
            Assert.False(ParsekFlight.TreeHasPlacedGroundPartsForIdle(tree, "IsActiveTreeIdleOnPad"));

            AddPlacement(tree, "Deployed Seismic Sensor", 5001u);
            Assert.True(ParsekFlight.TreeHasPlacedGroundPartsForIdle(tree, "IsActiveTreeIdleOnPad"));
            Assert.Contains(logLines, l =>
                l.Contains("IsActiveTreeIdleOnPad: not idle - tree has 1 placed ground part(s)"));
        }

        // The live-tree mirror (SceneExitInterceptor's pre-transition fast path) is an
        // instance method over a MonoBehaviour and cannot be driven headless; pin that it
        // applies the same veto before its flush and distance walk.
        [Fact]
        public void IsActiveTreeIdleOnPad_AppliesThePlacedPartVeto()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string source = File.ReadAllText(
                Path.Combine(projectRoot, "Source", "Parsek", "ParsekFlight.cs"))
                .Replace("\r\n", "\n");

            int start = source.IndexOf("internal bool IsActiveTreeIdleOnPad()\n", StringComparison.Ordinal);
            Assert.True(start >= 0, "IsActiveTreeIdleOnPad declaration not found");
            int walk = source.IndexOf("foreach (var rec in activeTree.Recordings.Values)", start, StringComparison.Ordinal);
            Assert.True(walk > start, "IsActiveTreeIdleOnPad distance walk not found");
            string head = source.Substring(start, walk - start);
            Assert.Contains("TreeHasPlacedGroundPartsForIdle(activeTree, \"IsActiveTreeIdleOnPad\")", head);
        }
    }
}
