using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // LOADER-FAULT rule. Pure in-memory AnalyzerModel fixtures (no loader, no
    // disk): the loader's job is only to POPULATE LoadFaults; this rule's job is to
    // turn the owned kinds into FAIL findings. Each test names the regression it
    // guards.
    public class LoadFaultRuleTests
    {
        private static AnalyzerModel ModelWith(params LoadFault[] faults)
        {
            return new AnalyzerModel
            {
                SaveName = "loadfault",
                LoadFaults = faults.ToList(),
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new LoadFaultRule().Evaluate(model).ToList();
        }

        // Guards the blocker: a corrupt persistent.sfs (the sfs LoadFault the loader
        // records) becomes a FAIL, so a corrupt save no longer analyzes GREEN. Fails
        // if the sfs kind is dropped and the report goes silent on a broken save.
        [Fact]
        public void SfsFault_Fails()
        {
            List<Finding> findings = Run(ModelWith(
                new LoadFault("/save/persistent.sfs", "sfs", "unbalanced-braces", null)));

            Finding fail = Assert.Single(findings);
            Assert.Equal(LoadFaultRule.RuleIdConst, fail.RuleId);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("kind=sfs", fail.Message);
            Assert.Contains("reason=unbalanced-braces", fail.Message);
            // Deterministic: the machine-specific directory never leaks into the
            // report; only the filename does.
            Assert.DoesNotContain("/save/", fail.Message);
            Assert.Contains("file=persistent.sfs", fail.Message);
        }

        // Guards: a throwing RECORDING tree node (tree-node LoadFault) becomes a
        // FAIL carrying the recording id. Fails if a corrupt tree record is silently
        // skipped with no finding.
        [Fact]
        public void TreeNodeFault_Fails()
        {
            List<Finding> findings = Run(ModelWith(
                new LoadFault("/save/persistent.sfs", "tree-node", "FormatException: bad", "rec-7")));

            Finding fail = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Equal("rec-7", fail.Target);
            Assert.Contains("kind=tree-node", fail.Message);
            Assert.Contains("recording=rec-7", fail.Message);
        }

        // Guards: an unparsable ledger.pgld (ledger LoadFault) becomes a FAIL. Fails
        // if a corrupt ledger analyzes GREEN.
        [Fact]
        public void LedgerFault_Fails()
        {
            List<Finding> findings = Run(ModelWith(
                new LoadFault("/save/Parsek/GameState/ledger.pgld", "ledger", "configNode-load-returned-null", null)));

            Finding fail = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Equal("<ledger>", fail.Target);
            Assert.Contains("kind=ledger", fail.Message);
        }

        // Guards single-report policy: trajectory and snapshot faults are owned by
        // INV5 / INV4 respectively, so this rule must NOT double-report them. Fails
        // if the owned-kind filter regresses and a .prec/.craft fault is counted
        // twice.
        [Fact]
        public void TrajectoryAndSnapshotFaults_NotReported()
        {
            List<Finding> findings = Run(ModelWith(
                new LoadFault("/save/Parsek/Recordings/r.prec", "trajectory", "truncated", "r"),
                new LoadFault("/save/Parsek/Recordings/r_vessel.craft", "snapshot", "snapshot-load-failed", "r")));

            Assert.Empty(findings);
        }

        // Guards: every owned fault produces its own FAIL (counts are not collapsed).
        // Fails if the rule emits a single aggregate finding and loses per-file
        // triage detail.
        [Fact]
        public void MultipleOwnedFaults_EachFails()
        {
            List<Finding> findings = Run(ModelWith(
                new LoadFault("/save/persistent.sfs", "sfs", "unbalanced-braces", null),
                new LoadFault("/save/Parsek/GameState/ledger.pgld", "ledger", "bad", null)));

            Assert.Equal(2, findings.Count);
            Assert.All(findings, f => Assert.Equal(VerdictLevel.Fail, f.Level));
        }

        // Guards: no faults / null list -> zero findings and no throw (clean saves
        // stay GREEN and the rule survives the core-purity in-memory model).
        [Fact]
        public void NoFaults_NoFindings_NoThrow()
        {
            Assert.Empty(Run(ModelWith()));
            Assert.Empty(new LoadFaultRule().Evaluate(new AnalyzerModel { SaveName = "empty" }));
        }
    }
}
