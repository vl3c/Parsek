using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV8 part (b): career-diff reconstruction. Pure in-memory fixtures build a
    // CareerSaveSnapshot and (where a reconstruction is injected) a
    // LedgerReconstructionSnapshot directly. Each test names the regression.
    public class Inv8LedgerCareerDiffTests
    {
        private static AnalyzerModel ModelWith(
            CareerSaveSnapshot career, LedgerReconstructionSnapshot recon)
        {
            return new AnalyzerModel
            {
                SaveName = "inv8-career",
                CareerSave = career,
                LedgerReconstruction = recon,
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv8Ledger().Evaluate(model).ToList();
        }

        private static CareerSaveSnapshot Career(double funds) =>
            new CareerSaveSnapshot { Parsed = true, HasFunds = true, Funds = funds };

        private static LedgerReconstructionSnapshot Recon(double funds) =>
            new LedgerReconstructionSnapshot { HasFunds = true, Funds = funds };

        // Guards: a career reconstruction that matches the parsed totals -> zero
        // INV8 career-diff findings. Fails if the LedgerGroundTruthDiff wiring is
        // wrong and a matching reconstruction diverges.
        [Fact]
        public void MatchingReconstruction_NoCareerDiffFinding()
        {
            List<Finding> findings = Run(ModelWith(Career(1000.0), Recon(1000.0)));

            Assert.DoesNotContain(findings, f => f.RuleId == Inv8Ledger.CareerDiffRuleId);
        }

        // Guards (edge case 16): a funds divergence beyond tolerance -> WARN, NEVER
        // FAIL offline (the FAIL-severity career diff is the in-game H5 path). Fails
        // if the offline analyzer promotes a career-diff divergence to FAIL.
        [Fact]
        public void FundsDivergence_Warns_NeverFails()
        {
            List<Finding> findings = Run(ModelWith(Career(1000.0), Recon(500.0)));

            Finding warn = Assert.Single(findings, f => f.RuleId == Inv8Ledger.CareerDiffRuleId);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.Contains("divergences=", warn.Message);
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }

        // Guards (correction C5): a career save with no injected reconstruction ->
        // INFO reconstruction-not-available, never FAIL/WARN. Fails if the offline
        // rule fabricates a reconstruction or hard-fails on the deferred seam.
        [Fact]
        public void CareerNoReconstruction_InfoReconstructionNotAvailable()
        {
            List<Finding> findings = Run(ModelWith(Career(1000.0), null));

            Finding info = Assert.Single(findings, f => f.RuleId == Inv8Ledger.CareerDiffRuleId);
            Assert.Equal(VerdictLevel.Info, info.Level);
            Assert.Contains("reconstruction-not-available", info.Message);
        }

        // Guards (edge case 18): a non-career save skips part (b) entirely -> no
        // career-diff finding. Fails if the ledger rule crashes or fabricates a
        // career-diff finding on a null CareerSaveSnapshot.
        [Fact]
        public void NonCareer_NoCareerDiffFinding()
        {
            List<Finding> findings = Run(ModelWith(null, null));

            Assert.DoesNotContain(findings, f => f.RuleId == Inv8Ledger.CareerDiffRuleId);
        }
    }
}
