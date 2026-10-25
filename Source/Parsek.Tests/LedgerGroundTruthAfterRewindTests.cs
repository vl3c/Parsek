using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo LEDGER-GROUND-TRUTH-WALKS-PAST-THE-CLOCK: after a rewind or a Re-Fly the ledger
    /// holds committed rows ahead of the clock, and the save holds the career at now. The
    /// ground-truth harness walked the whole ledger, so a committed future reputation loss
    /// read as a hard divergence on a correct pool (and a future gain was hidden). It now
    /// walks with the current-UT cutoff, as every production patch does.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerGroundTruthAfterRewindTests : IDisposable
    {
        public LedgerGroundTruthAfterRewindTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecalculationEngine.ClearModules();
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = false;
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = false;
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>Reputation 10 at the seed, and a committed flight (after the rewind
        /// clock of 100) that fails an accepted contract at 500 for 5 reputation.</summary>
        private static List<GameAction> LedgerWithAFutureReputationLoss()
        {
            return new List<GameAction>
            {
                new GameAction { UT = 0.0, Type = GameActionType.ReputationInitial, InitialReputation = 10f },
                new GameAction
                {
                    UT = 50.0, Type = GameActionType.ContractAccept, ContractId = "c1",
                    DeadlineUT = 1.0e6, FundsPenalty = 0f, RepPenalty = 5f,
                },
                new GameAction
                {
                    UT = 500.0, Type = GameActionType.ContractFail, ContractId = "c1",
                    FundsPenalty = 0f, RepPenalty = 5f, RecordingId = "rec-committed",
                },
            };
        }

        private static double WalkReputation(double? cutoff)
        {
            RecalculationEngine.ClearModules();
            var contracts = new ContractsModule();
            var rep = new ReputationModule();
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(rep, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.Recalculate(LedgerWithAFutureReputationLoss(), cutoff);
            return rep.GetRunningRep();
        }

        private static LedgerDivergenceReport CompareReputation(double reconRep)
        {
            var save = new CareerSaveSnapshot { Parsed = true, HasRep = true, Reputation = 10.0 };
            var recon = new LedgerReconstructionSnapshot { HasRep = true, Reputation = reconRep };
            return LedgerGroundTruthDiff.Compare(save, recon, FacetTolerances.Default,
                new Dictionary<string, int>());
        }

        // catches: the harness walk reading the end-of-timeline reputation (5) against the
        // save's reputation at the rewind clock (10).
        [Fact]
        public void CutoffWalk_FutureReputationLoss_MatchesTheSaveAtNow()
        {
            double rep = WalkReputation(100.0);

            Assert.Equal(10.0, rep, 3);
            Assert.Empty(CompareReputation(rep).HardFailures(false));
        }

        [Fact]
        public void FullWalk_FutureReputationLoss_ReadsAsAHardDivergence_DocumentsTheOldHarnessRead()
        {
            double rep = WalkReputation(null);

            Assert.True(rep < 10.0 - 1.0, "the full walk applies the committed future loss");
            Assert.NotEmpty(CompareReputation(rep).HardFailures(false));
        }

        // catches: the harness going back to a cutoff-less RecalculateAndPatch().
        [Fact]
        public void Harness_WalksThroughTheCurrentTimelineHelper()
        {
            string path = Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "InGameTests", "LedgerGroundTruthHarness.cs");
            string src = File.ReadAllText(Path.GetFullPath(path));

            Assert.Contains("RecalculateAndPatchForCurrentTimelineIfFutureActions(", src);
            Assert.DoesNotContain("LedgerOrchestrator.RecalculateAndPatch();", src);
        }

        // catches: the reconstruction's ELS scans reading a committed tech purchase or a
        // recovery credit that lies ahead of the clock (L13 run 2026-10-25_0144: basicRocketry,
        // researched at UT 1000, read as researched at UT 450).
        [Fact]
        public void ElsScans_LeaveOutRowsAheadOfTheClock()
        {
            var past = new GameAction { UT = 400.0, Type = GameActionType.ScienceSpending, NodeId = "start" };
            var atClock = new GameAction { UT = 450.0, Type = GameActionType.ScienceSpending, NodeId = "x" };
            var future = new GameAction { UT = 1000.0, Type = GameActionType.ScienceSpending, NodeId = "basicRocketry" };

            Assert.True(Parsek.InGameTests.LedgerGroundTruthHarness.IsAtOrBeforeClock(past, 450.0));
            Assert.True(Parsek.InGameTests.LedgerGroundTruthHarness.IsAtOrBeforeClock(atClock, 450.0));
            Assert.False(Parsek.InGameTests.LedgerGroundTruthHarness.IsAtOrBeforeClock(future, 450.0));
            Assert.True(Parsek.InGameTests.LedgerGroundTruthHarness.IsAtOrBeforeClock(future, double.NaN));
            Assert.False(Parsek.InGameTests.LedgerGroundTruthHarness.IsAtOrBeforeClock(null, 450.0));
        }

        // catches: the harness scans going back to the whole ledger.
        [Fact]
        public void Harness_ElsScansTakeTheWalkClock()
        {
            string path = Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "InGameTests", "LedgerGroundTruthHarness.cs");
            string src = File.ReadAllText(Path.GetFullPath(path));

            Assert.Contains("ScanResearchedTechIds(clockUT, out bool hasTechSurface)", src);
            Assert.Contains("ScanRecoveryCredits(clockUT)", src);
            Assert.Equal(2, CountOccurrences(src, "if (!IsAtOrBeforeClock(a, clockUT))"));
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        // catches: a sibling cell of the LedgerGroundTruth batch walking past the clock. The
        // currency tooltip cell did (L13 run 2026-10-25_0144): its cutoff-less walk wrote a
        // committed future contract advance onto the live funds after an F9, and the
        // ground-truth cell that ran next saved the career and read it as a hard divergence.
        [Fact]
        public void EveryLedgerGroundTruthCell_WalksThroughTheCurrentTimelineHelper()
        {
            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "InGameTests"));
            var cells = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(file);
                if (!src.Contains("Category = \"LedgerGroundTruth\"")) continue;
                cells.Add(Path.GetFileName(file));
                Assert.False(src.Contains("LedgerOrchestrator.RecalculateAndPatch();"),
                    Path.GetFileName(file) + " walks the ledger past the clock");
            }

            Assert.Contains("LedgerGroundTruthHarness.cs", cells);
            Assert.Contains("CurrencyTooltipLiveInvariantTest.cs", cells);
            Assert.Contains("KerbalExperienceReassertTest.cs", cells);
        }
    }
}
