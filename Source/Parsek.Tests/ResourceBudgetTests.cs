using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ResourceBudgetTests
    {
        public ResourceBudgetTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        #region PreLaunch Field Propagation

        [Fact]
        public void PreLaunchFields_SurviveApplyPersistenceArtifacts()
        {
            var source = new Recording
            {
                RecordingId = "src1",
                PreLaunchFunds = 50000,
                PreLaunchScience = 100,
                PreLaunchReputation = 75
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(50000, target.PreLaunchFunds);
            Assert.Equal(100, target.PreLaunchScience);
            Assert.Equal(75, target.PreLaunchReputation);
        }

        [Fact]
        public void PreLaunchFields_MetadataRoundTrip()
        {
            var source = new Recording
            {
                RecordingId = "meta1",
                PreLaunchFunds = 45000.5,
                PreLaunchScience = 123.456,
                PreLaunchReputation = 67.89f
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.Equal(45000.5, loaded.PreLaunchFunds);
            Assert.Equal(123.456, loaded.PreLaunchScience, 3);
            Assert.Equal(67.89f, loaded.PreLaunchReputation, 0.01f);
        }

        [Fact]
        public void PreLaunchFields_MissingKeysDefaultToZero()
        {
            var node = RecordingCodecTestNodes.BareCurrentContract("prelaunch-missing");
            Assert.Null(node.GetValue("preLaunchFunds"));
            Assert.Null(node.GetValue("preLaunchScience"));
            Assert.Null(node.GetValue("preLaunchRep"));

            var loaded = RecordingCodecTestNodes.LoadPastSchemaGate(node);

            Assert.Equal(0, loaded.PreLaunchFunds);
            Assert.Equal(0, loaded.PreLaunchScience);
            Assert.Equal(0, loaded.PreLaunchReputation);
        }

        #endregion

        // The per-recording and per-milestone cost helpers (Committed*Cost,
        // MilestoneCommitted*, FullCommitted*Cost, ComputeFacilityUpgradeCost)
        // and the ComputeTotal / ComputeTotalFullCost aggregators had no
        // production caller and are deleted with their cells; the
        // FinalizeTreeCommit shape fact one aggregator cell used to carry is
        // pinned directly in RecordingStoreTests.

        #region RecordingPaths

        [Fact]
        public void BuildMilestonesRelativePath_CorrectPath()
        {
            string path = RecordingPaths.BuildMilestonesRelativePath().Replace('\\', '/');
            Assert.Equal("Parsek/GameState/milestones.pgsm", path);
        }

        #endregion
    }
}
