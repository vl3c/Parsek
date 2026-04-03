using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class LedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public LedgerTests()
        {
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_ledger_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Ledger.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private string LedgerPath => Path.Combine(tempDir, "ledger.pgld");

        // ================================================================
        // AddAction
        // ================================================================

        [Fact]
        public void AddAction_AppendsToList()
        {
            var action = new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_001",
                SubjectId = "crewReport@KerbinSrfLandedLaunchPad"
            };

            Ledger.AddAction(action);

            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Same(action, Ledger.Actions[0]);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Added action") && l.Contains("ScienceEarning"));
        }

        [Fact]
        public void AddAction_NullAction_Skipped()
        {
            Ledger.AddAction(null);

            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("null action"));
        }

        [Fact]
        public void AddActions_BatchAppend()
        {
            var batch = new[]
            {
                new GameAction { UT = 17000.0, Type = GameActionType.ScienceEarning, RecordingId = "rec_001" },
                new GameAction { UT = 17100.0, Type = GameActionType.FundsEarning, RecordingId = "rec_001" },
                new GameAction { UT = 17200.0, Type = GameActionType.ScienceSpending }
            };

            Ledger.AddActions(batch);

            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("AddActions batch") && l.Contains("added=3"));
        }

        [Fact]
        public void AddActions_NullCollection_Skipped()
        {
            Ledger.AddActions(null);

            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("null collection"));
        }

        [Fact]
        public void Clear_RemovesAllActions()
        {
            Ledger.AddAction(new GameAction { UT = 100.0, Type = GameActionType.FundsEarning });
            Ledger.AddAction(new GameAction { UT = 200.0, Type = GameActionType.FundsSpending });

            Ledger.Clear();

            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Cleared ledger") && l.Contains("removed=2"));
        }

        // ================================================================
        // Save and Load Round-Trip
        // ================================================================

        [Fact]
        public void SaveAndLoad_RoundTrip()
        {
            // Populate with diverse action types
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_001",
                SubjectId = "crewReport@MunSrfLandedMidlands",
                ExperimentId = "crewReport",
                Body = "Mun",
                Situation = "SrfLanded",
                Biome = "Midlands",
                ScienceAwarded = 8.5f,
                Method = ScienceMethod.Recovered,
                TransmitScalar = 1.0f,
                SubjectMaxValue = 15.0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17100.0,
                Type = GameActionType.ScienceSpending,
                Sequence = 1,
                NodeId = "survivability",
                Cost = 5.0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17200.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec_002",
                FundsAwarded = 10000f,
                FundsSource = FundsEarningSource.ContractComplete
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17300.0,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = "rec_001",
                MilestoneId = "ReachedSpace",
                MilestoneFundsAwarded = 5000f,
                MilestoneRepAwarded = 10f
            });

            // Save
            bool saveOk = Ledger.SaveToFile(LedgerPath);
            Assert.True(saveOk);
            Assert.True(File.Exists(LedgerPath));

            // Load into fresh state
            Ledger.ResetForTesting();
            logLines.Clear();

            bool loadOk = Ledger.LoadFromFile(LedgerPath);
            Assert.True(loadOk);
            Assert.Equal(5, Ledger.Actions.Count);

            // Verify FundsInitial
            var funds = Ledger.Actions[0];
            Assert.Equal(GameActionType.FundsInitial, funds.Type);
            Assert.Equal(0.0, funds.UT);
            Assert.Equal(25000f, funds.InitialFunds);

            // Verify ScienceEarning
            var science = Ledger.Actions[1];
            Assert.Equal(GameActionType.ScienceEarning, science.Type);
            Assert.Equal(17000.0, science.UT);
            Assert.Equal("rec_001", science.RecordingId);
            Assert.Equal("crewReport@MunSrfLandedMidlands", science.SubjectId);
            Assert.Equal(8.5f, science.ScienceAwarded);
            Assert.Equal(ScienceMethod.Recovered, science.Method);

            // Verify ScienceSpending
            var spend = Ledger.Actions[2];
            Assert.Equal(GameActionType.ScienceSpending, spend.Type);
            Assert.Equal(17100.0, spend.UT);
            Assert.Equal(1, spend.Sequence);
            Assert.Equal("survivability", spend.NodeId);
            Assert.Equal(5.0f, spend.Cost);
            Assert.Null(spend.RecordingId);

            // Verify FundsEarning
            var fundsE = Ledger.Actions[3];
            Assert.Equal(GameActionType.FundsEarning, fundsE.Type);
            Assert.Equal("rec_002", fundsE.RecordingId);
            Assert.Equal(10000f, fundsE.FundsAwarded);

            // Verify MilestoneAchievement
            var milestone = Ledger.Actions[4];
            Assert.Equal(GameActionType.MilestoneAchievement, milestone.Type);
            Assert.Equal("ReachedSpace", milestone.MilestoneId);
            Assert.Equal(5000f, milestone.MilestoneFundsAwarded);

            // Verify log output
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Loaded ledger") && l.Contains("actions=5"));
        }

        // ================================================================
        // Reconcile — Earning pruning
        // ================================================================

        [Fact]
        public void Reconcile_PrunesOrphanedEarnings()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_deleted"
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17100.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec_deleted"
            });

            var valid = new HashSet<string> { "rec_001", "rec_002" };
            Ledger.Reconcile(valid, 99999.0);

            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("prunedEarnings=2"));
        }

        [Fact]
        public void Reconcile_KeepsValidEarnings()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceEarning,
                RecordingId = "rec_001"
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17100.0,
                Type = GameActionType.FundsEarning,
                RecordingId = "rec_002"
            });

            var valid = new HashSet<string> { "rec_001", "rec_002" };
            Ledger.Reconcile(valid, 99999.0);

            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("kept=2") && l.Contains("prunedEarnings=0"));
        }

        // ================================================================
        // Reconcile — Spending pruning
        // ================================================================

        [Fact]
        public void Reconcile_PrunesFutureSpendings()
        {
            // Spending (no recordingId) at UT > maxUT
            Ledger.AddAction(new GameAction
            {
                UT = 18000.0,
                Type = GameActionType.ScienceSpending,
                NodeId = "advRocketry"
            });
            Ledger.AddAction(new GameAction
            {
                UT = 19000.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 500f
            });

            var valid = new HashSet<string>();
            Ledger.Reconcile(valid, 17500.0);

            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("prunedSpendings=2"));
        }

        [Fact]
        public void Reconcile_KeepsCurrentSpendings()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.ScienceSpending,
                NodeId = "basicRocketry"
            });

            var valid = new HashSet<string>();
            Ledger.Reconcile(valid, 18000.0);

            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Equal("basicRocketry", Ledger.Actions[0].NodeId);
        }

        [Fact]
        public void Reconcile_BoundaryUT_Kept()
        {
            // Spending at exactly maxUT should be kept (pruning is strictly > maxUT)
            Ledger.AddAction(new GameAction
            {
                UT = 17500.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 300f
            });

            var valid = new HashSet<string>();
            Ledger.Reconcile(valid, 17500.0);

            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Equal(17500.0, Ledger.Actions[0].UT);
        }

        [Fact]
        public void Reconcile_SpendingWithNoRecordingId_PrunedByUT()
        {
            // Spending actions are identified by null recordingId, pruned by UT
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "LaunchPad",
                ToLevel = 2,
                FacilityCost = 15000f
                // No RecordingId — this is a KSC spending
            });
            Ledger.AddAction(new GameAction
            {
                UT = 20000.0,
                Type = GameActionType.KerbalHire,
                KerbalName = "Bob Kerman",
                HireCost = 10000f
                // No RecordingId — this is a KSC spending
            });

            var valid = new HashSet<string>();
            Ledger.Reconcile(valid, 18000.0);

            // First action at UT=17000 kept (<=18000), second at UT=20000 pruned (>18000)
            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Equal(GameActionType.FacilityUpgrade, Ledger.Actions[0].Type);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("prunedSpendings=1") && l.Contains("kept=1"));
        }

        [Fact]
        public void Reconcile_FundsInitial_AlwaysKept()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });

            // Even with empty valid set and maxUT=0, FundsInitial survives
            var valid = new HashSet<string>();
            Ledger.Reconcile(valid, 0.0);

            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Equal(GameActionType.FundsInitial, Ledger.Actions[0].Type);
        }

        [Fact]
        public void Reconcile_MixedActions_CorrectPruning()
        {
            // Earning with valid recordingId — kept
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0, Type = GameActionType.ScienceEarning, RecordingId = "rec_001"
            });
            // Earning with orphaned recordingId — pruned
            Ledger.AddAction(new GameAction
            {
                UT = 17100.0, Type = GameActionType.FundsEarning, RecordingId = "rec_deleted"
            });
            // Spending within maxUT — kept
            Ledger.AddAction(new GameAction
            {
                UT = 17200.0, Type = GameActionType.ScienceSpending
            });
            // Spending after maxUT — pruned
            Ledger.AddAction(new GameAction
            {
                UT = 20000.0, Type = GameActionType.FundsSpending
            });
            // FundsInitial — always kept
            Ledger.AddAction(new GameAction
            {
                UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 25000f
            });

            var valid = new HashSet<string> { "rec_001" };
            Ledger.Reconcile(valid, 18000.0);

            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("prunedEarnings=1") && l.Contains("prunedSpendings=1"));
        }

        // ================================================================
        // SeedInitialFunds
        // ================================================================

        [Fact]
        public void SeedInitialFunds_CreatesAction()
        {
            Ledger.SeedInitialFunds(25000.0);

            Assert.Equal(1, Ledger.Actions.Count);
            var seed = Ledger.Actions[0];
            Assert.Equal(GameActionType.FundsInitial, seed.Type);
            Assert.Equal(0.0, seed.UT);
            Assert.Equal(25000f, seed.InitialFunds);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Seeded initial funds") && l.Contains("25000"));
        }

        [Fact]
        public void SeedInitialFunds_NoDuplicate()
        {
            Ledger.SeedInitialFunds(25000.0);
            logLines.Clear();

            // Second call with different amount — should NOT create a new action
            Ledger.SeedInitialFunds(50000.0);

            Assert.Equal(1, Ledger.Actions.Count);
            Assert.Equal(25000f, Ledger.Actions[0].InitialFunds);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("already exists") && l.Contains("ignoring"));
        }

        // ================================================================
        // Load edge cases
        // ================================================================

        [Fact]
        public void LoadFromFile_EmptyFile_ReturnsEmpty()
        {
            // Create an empty (but valid) ledger file with no actions
            var root = new ConfigNode("LEDGER");
            root.AddValue("version", "1");
            root.Save(LedgerPath);

            bool ok = Ledger.LoadFromFile(LedgerPath);

            Assert.True(ok);
            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Loaded ledger") && l.Contains("actions=0"));
        }

        [Fact]
        public void LoadFromFile_CorruptFile_LogsWarning()
        {
            // Write garbage to the file
            File.WriteAllText(LedgerPath, "THIS IS NOT A VALID CONFIGNODE!!!");

            bool ok = Ledger.LoadFromFile(LedgerPath);

            // ConfigNode.Load returns null for garbage — our code handles this
            Assert.False(ok);
            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("WARN"));
        }

        [Fact]
        public void LoadFromFile_MissingFile_EmptyLedger()
        {
            string missingPath = Path.Combine(tempDir, "nonexistent.pgld");

            bool ok = Ledger.LoadFromFile(missingPath);

            Assert.True(ok); // Missing file is not an error — it's a fresh save
            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("not found") && l.Contains("empty ledger"));
        }

        [Fact]
        public void LoadFromFile_BadVersion_LogsWarning()
        {
            var root = new ConfigNode("LEDGER");
            root.AddValue("version", "0");
            root.Save(LedgerPath);

            bool ok = Ledger.LoadFromFile(LedgerPath);

            Assert.False(ok);
            Assert.Equal(0, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("unsupported version"));
        }

        // ================================================================
        // Safe-write pattern
        // ================================================================

        [Fact]
        public void SaveToFile_SafeWrite_AtomicRename()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });

            bool ok = Ledger.SaveToFile(LedgerPath);

            Assert.True(ok);
            // The final file should exist
            Assert.True(File.Exists(LedgerPath));
            // The .tmp file should NOT exist (was renamed to final)
            Assert.False(File.Exists(LedgerPath + ".tmp"));

            // Verify the file is valid by loading it
            Ledger.ResetForTesting();
            bool loadOk = Ledger.LoadFromFile(LedgerPath);
            Assert.True(loadOk);
            Assert.Equal(1, Ledger.Actions.Count);
        }

        [Fact]
        public void SaveToFile_OverwritesExistingFile()
        {
            // First save
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0, Type = GameActionType.FundsInitial, InitialFunds = 25000f
            });
            Ledger.SaveToFile(LedgerPath);

            // Second save with different content
            Ledger.ResetForTesting();
            Ledger.AddAction(new GameAction
            {
                UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 50000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 18000.0, Type = GameActionType.ScienceEarning, RecordingId = "rec_001"
            });
            Ledger.SaveToFile(LedgerPath);

            // Load and verify it's the second version
            Ledger.ResetForTesting();
            Ledger.LoadFromFile(LedgerPath);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Equal(50000f, Ledger.Actions[0].InitialFunds);
        }

        [Fact]
        public void SaveToFile_NullPath_ReturnsFalse()
        {
            Ledger.AddAction(new GameAction { UT = 100.0, Type = GameActionType.FundsInitial });

            bool ok = Ledger.SaveToFile(null);

            Assert.False(ok);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("null/empty path"));
        }

        // ================================================================
        // Log assertion tests for reconciliation
        // ================================================================

        [Fact]
        public void Reconcile_LogsEarningPruning()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 17000.0, Type = GameActionType.ScienceEarning, RecordingId = "orphan_1"
            });
            Ledger.AddAction(new GameAction
            {
                UT = 17100.0, Type = GameActionType.FundsEarning, RecordingId = "orphan_2"
            });
            logLines.Clear();

            Ledger.Reconcile(new HashSet<string>(), 99999.0);

            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Reconcile complete") &&
                l.Contains("prunedEarnings=2") && l.Contains("kept=0"));
        }

        [Fact]
        public void Reconcile_LogsSpendingPruning()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 20000.0, Type = GameActionType.ScienceSpending
            });
            logLines.Clear();

            Ledger.Reconcile(new HashSet<string>(), 17000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("Reconcile complete") &&
                l.Contains("prunedSpendings=1"));
        }

        [Fact]
        public void Reconcile_NullValidIds_LogsWarning()
        {
            Ledger.Reconcile(null, 17000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("null validRecordingIds"));
        }

        // ================================================================
        // BuildLedgerRelativePath
        // ================================================================

        [Fact]
        public void BuildLedgerRelativePath_ReturnsCorrectPath()
        {
            string path = RecordingPaths.BuildLedgerRelativePath();

            // Should be Parsek/GameState/ledger.pgld (platform-specific separator)
            Assert.Contains("Parsek", path);
            Assert.Contains("GameState", path);
            Assert.Contains("ledger.pgld", path);
        }

        // ================================================================
        // SeedInitialScience / SeedInitialReputation (D19)
        // ================================================================

        [Fact]
        public void SeedInitialScience_CreatesActionOnce()
        {
            Ledger.ResetForTesting();

            Ledger.SeedInitialScience(150f);
            Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.ScienceInitial, Ledger.Actions[0].Type);
            Assert.Equal(150f, Ledger.Actions[0].InitialScience);

            // Second call should be no-op
            Ledger.SeedInitialScience(200f);
            Assert.Single(Ledger.Actions);
            Assert.Equal(150f, Ledger.Actions[0].InitialScience);
        }

        [Fact]
        public void SeedInitialReputation_CreatesActionOnce()
        {
            Ledger.ResetForTesting();

            Ledger.SeedInitialReputation(87.5f);
            Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.ReputationInitial, Ledger.Actions[0].Type);
            Assert.Equal(87.5f, Ledger.Actions[0].InitialReputation);

            // Second call should be no-op
            Ledger.SeedInitialReputation(100f);
            Assert.Single(Ledger.Actions);
            Assert.Equal(87.5f, Ledger.Actions[0].InitialReputation);
        }

        [Fact]
        public void Reconcile_PreservesSeedActions()
        {
            Ledger.ResetForTesting();

            Ledger.SeedInitialFunds(10000);
            Ledger.SeedInitialScience(50f);
            Ledger.SeedInitialReputation(25f);

            // Reconcile with no valid recordings and UT=0
            Ledger.Reconcile(new System.Collections.Generic.HashSet<string>(), 0);

            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FundsInitial);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.ScienceInitial);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.ReputationInitial);
        }
    }
}
