using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ledger ground truth in a Science game (CS-2): the in-game harness runs in
    /// SCIENCE_SANDBOX with only the R&amp;D singleton, and the diff over a Science save
    /// (no Funding / Reputation scenario, so no funds or rep facet) compares the science
    /// pool HARD and never reads the reconstruction's funds or rep.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerGroundTruthScienceModeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerGroundTruthScienceModeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = false;
        }

        public void Dispose()
        {
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ModeSkip_ScienceGameWithResearch_Runs()
        {
            // Stock creates no Funding / Reputation in a Science game, so those two
            // singletons are null there; requiring them skipped every Science run.
            Assert.Null(LedgerGroundTruthHarness.GroundTruthModeSkipReason(
                Game.Modes.SCIENCE_SANDBOX, hasFunding: false, hasResearch: true, hasReputation: false));
        }

        [Fact]
        public void ModeSkip_ScienceGameWithoutResearch_Skips()
        {
            Assert.Equal("R&D singleton is not initialized",
                LedgerGroundTruthHarness.GroundTruthModeSkipReason(
                    Game.Modes.SCIENCE_SANDBOX, hasFunding: false, hasResearch: false, hasReputation: false));
        }

        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, false, false)]
        public void ModeSkip_CareerNeedsAllThreePools(
            bool hasFunding, bool hasResearch, bool hasReputation, bool runs)
        {
            string reason = LedgerGroundTruthHarness.GroundTruthModeSkipReason(
                Game.Modes.CAREER, hasFunding, hasResearch, hasReputation);
            if (runs)
                Assert.Null(reason);
            else
                Assert.Equal("Funding/R&D/Reputation singletons are not all initialized", reason);
        }

        [Fact]
        public void ModeSkip_SandboxSkipsNamingTheMode()
        {
            string reason = LedgerGroundTruthHarness.GroundTruthModeSkipReason(
                Game.Modes.SANDBOX, hasFunding: true, hasResearch: true, hasReputation: true);
            Assert.Equal("Ledger ground-truth verification needs a career or science game (mode=SANDBOX)", reason);
        }

        [Fact]
        public void PoolsForMode_ScienceIsScienceOnly()
        {
            Assert.Equal("science", LedgerGroundTruthHarness.GroundTruthPoolsForMode(Game.Modes.SCIENCE_SANDBOX));
            Assert.Equal("funds,science,reputation", LedgerGroundTruthHarness.GroundTruthPoolsForMode(Game.Modes.CAREER));
        }

        private static CareerSaveSnapshot ScienceSave(double science)
        {
            return new CareerSaveSnapshot
            {
                Parsed = true,
                HasFunds = false,
                HasScience = true,
                SciencePool = science,
                HasRep = false
            };
        }

        private static LedgerReconstructionSnapshot Recon(double science)
        {
            // The harness always fills the three running values; in a Science game the
            // funds / rep modules hold whatever the walk left, never compared.
            return new LedgerReconstructionSnapshot
            {
                HasFunds = true,
                Funds = 123456.0,
                HasScience = true,
                SciencePool = science,
                HasRep = true,
                Reputation = 42.0
            };
        }

        [Fact]
        public void Diff_ScienceSave_MatchingPool_NoHardFailure_AndNoFundsOrRepFacet()
        {
            var report = LedgerGroundTruthDiff.Compare(
                ScienceSave(13.5), Recon(13.5), FacetTolerances.Default, new Dictionary<string, int>());

            Assert.Empty(report.HardFailures(strict: false));
            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Funds);
            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Reputation);
            Assert.Contains(logLines, l => l.Contains("CompareFunds") && l.Contains("skip"));
            Assert.Contains(logLines, l => l.Contains("CompareReputation") && l.Contains("skip"));
        }

        [Fact]
        public void Diff_ScienceSave_PoolBelowRecon_IsHard()
        {
            // The science pool stays a seeded HARD facet in a Science game: a ledger that
            // never booked the Engineering 101 debit reads 18.5 against KSP's 13.5.
            var report = LedgerGroundTruthDiff.Compare(
                ScienceSave(13.5), Recon(18.5), FacetTolerances.Default, new Dictionary<string, int>(),
                authoritativeReduction: true);

            Assert.Contains(report.HardFailures(strict: false), d => d.Facet == DivergenceFacet.SciencePool);
        }
    }
}
