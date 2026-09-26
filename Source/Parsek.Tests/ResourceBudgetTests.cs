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

        #region ParseCostFromDetail

        [Fact]
        public void ParseCostFromDetail_SimpleCost()
        {
            Assert.Equal(600, ResourceBudget.ParseCostFromDetail("cost=600"));
        }

        [Fact]
        public void ParseCostFromDetail_WithOtherFields()
        {
            Assert.Equal(5, ResourceBudget.ParseCostFromDetail("cost=5;parts=solidBooster.sm.v2"));
        }

        [Fact]
        public void ParseCostFromDetail_Empty()
        {
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail(""));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail(null));
        }

        [Fact]
        public void ParseCostFromDetail_NoCostField()
        {
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("type=SurveyContract"));
        }

        [Fact]
        public void ParseCostFromDetail_InvalidFloat()
        {
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost=abc"));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost="));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost=12.34.56"));
        }

        [Fact]
        public void ParseCostFromDetail_NegativeCost()
        {
            Assert.Equal(-500, ResourceBudget.ParseCostFromDetail("cost=-500"));
        }

        [Fact]
        public void ParseCostFromDetail_DecimalCost()
        {
            Assert.Equal(12.5, ResourceBudget.ParseCostFromDetail("cost=12.5"));
        }

        [Fact]
        public void ParseCostFromDetail_CaseSensitive()
        {
            // "cost=" is case-sensitive (Ordinal comparison)
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("Cost=600"));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("COST=600"));
        }

        [Fact]
        public void ParseCostFromDetail_DuplicateCostField()
        {
            // First match wins
            Assert.Equal(100, ResourceBudget.ParseCostFromDetail("cost=100;cost=200"));
        }

        [Fact]
        public void ParseCostFromDetail_CostInMiddle()
        {
            Assert.Equal(300, ResourceBudget.ParseCostFromDetail("type=tech;cost=300;node=basic"));
        }

        // ---------- #451: charged cost is authoritative; entryCost is fallback-only ----------

        [Fact]
        public void ParseCostFromDetail_UsesCostWhenEntryCostAlsoPresent()
        {
            // Mirrors GameStateEventConverter.ConvertPartPurchased: `cost=` is the
            // authoritative charged amount, and `entryCost=` is only raw-price context.
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost=0;entryCost=800"));
        }

        [Fact]
        public void ParseCostFromDetail_EntryCostOnly_FallsBackWhenCostMissing()
        {
            Assert.Equal(1200, ResourceBudget.ParseCostFromDetail("entryCost=1200"));
        }

        [Fact]
        public void ParseCostFromDetail_CostWinsEvenWhenEntryCostAppearsFirst()
        {
            Assert.Equal(450, ResourceBudget.ParseCostFromDetail("entryCost=800;cost=450"));
        }

        #endregion

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
