using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    /// <summary>
    /// A configurable in-memory rule used to prove verdict policy without a real
    /// invariant. Parameterless-constructible (so the CitedContract-presence
    /// reflection test can instantiate it) and pure (records nothing from disk),
    /// so it is a valid IRecordingInvariant implementation in its own right.
    /// </summary>
    internal sealed class StubRule : IRecordingInvariant
    {
        public string RuleId { get; set; } = "STUB-RULE";
        public string CitedContract { get; set; } = "StubContract";
        public List<Finding> FindingsToReturn { get; set; } = new List<Finding>();

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            return FindingsToReturn;
        }
    }

    public class InvariantRegistryTests
    {
        private static AnalyzerModel InMemoryModel()
        {
            // Fully in-memory: no loader, no disk. The core must run over this.
            return new AnalyzerModel
            {
                SaveName = "mem",
                Recordings = new List<Recording>
                {
                    new Recording { RecordingId = "rec-1", RecordingSchemaGeneration = 4 },
                },
            };
        }

        private static List<IRecordingInvariant> Rules(params Finding[] findings)
        {
            return new List<IRecordingInvariant>
            {
                new StubRule { FindingsToReturn = findings.ToList() },
            };
        }

        // Guards: a FAIL finding lands as Counts.Fail and marks the run red. If FAIL
        // were downgraded, CI would go green on real corruption.
        [Fact]
        public void Evaluate_FailFinding_CountsFail_AndRunIsRed()
        {
            var report = InvariantEvaluator.Evaluate(
                InMemoryModel(),
                Rules(new Finding("INV1-UT-MONOTONIC", VerdictLevel.Fail, "rec-1", -1, "m", "c")));

            Assert.Equal(1, report.Counts.Fail);
            Assert.True(report.IsRed);
        }

        // Guards: WARN-only does not fail the run but is surfaced. A regression that
        // escalated WARN would block unrelated PRs.
        [Fact]
        public void Evaluate_WarnOnly_NotRed_ButSurfaced()
        {
            var report = InvariantEvaluator.Evaluate(
                InMemoryModel(),
                Rules(new Finding("INV3-ABSOLUTE-RANGE", VerdictLevel.Warn, "rec-1", -1, "m", "c")));

            Assert.Equal(0, report.Counts.Fail);
            Assert.Equal(1, report.Counts.Warn);
            Assert.False(report.IsRed);
            Assert.Single(report.Findings);
        }

        // Guards: INFO never fails a run (inventory / provenance).
        [Fact]
        public void Evaluate_InfoOnly_NotRed()
        {
            var report = InvariantEvaluator.Evaluate(
                InMemoryModel(),
                Rules(new Finding("INV6-RESOURCE-MANIFEST", VerdictLevel.Info, "rec-1", -1, "m", "c")));

            Assert.Equal(1, report.Counts.Info);
            Assert.False(report.IsRed);
        }

        // Guards: STALE-FIXTURE is distinct from FAIL - it marks the run red but as
        // a stale-corpus maintenance failure, not a code bug (no FAIL count).
        [Fact]
        public void Evaluate_StaleFixture_RedButDistinctFromFail()
        {
            var report = InvariantEvaluator.Evaluate(
                InMemoryModel(),
                Rules(new Finding("FIX-STALE", VerdictLevel.StaleFixture, "rec-1", -1, "m", "c")));

            Assert.Equal(0, report.Counts.Fail);
            Assert.Equal(1, report.Counts.StaleFixture);
            Assert.True(report.IsRed);
        }

        // Guards: the shipped registry carries the registered production rules, and
        // Evaluate over the default rule set produces a clean, green report on a
        // well-formed in-memory model (a rule that false-alarms on clean data would
        // turn this red). Phase 0/1 asserted an empty registry; from Phase 2 the
        // registry is non-empty and this pins "clean data -> no findings".
        [Fact]
        public void Evaluate_DefaultRegistry_HasRules_AndCleanModelIsGreen()
        {
            Assert.NotEmpty(InvariantRegistry.AllRules);

            var report = InvariantEvaluator.Evaluate(InMemoryModel());
            Assert.Empty(report.Findings);
            Assert.False(report.IsRed);
        }

        // Guards: SubjectSchemaGeneration is discovered from the recordings, not
        // hardcoded, so the report reflects what the save actually carries.
        [Fact]
        public void Evaluate_DiscoversSubjectSchemaGeneration_FromRecordings()
        {
            var withRecordings = InvariantEvaluator.Evaluate(InMemoryModel());
            Assert.Equal(4, withRecordings.SubjectSchemaGeneration);

            var empty = InvariantEvaluator.Evaluate(new AnalyzerModel { SaveName = "empty" });
            Assert.Equal(0, empty.SubjectSchemaGeneration);
        }

        // Guards (H5 readiness): every rule runs over a fully in-memory model with
        // no loader. Fails if a rule reaches for a file/Stream, which would block the
        // future in-game RecordingInvariants category from reusing the core. With
        // the empty production set this proves the wiring; every rule added later is
        // exercised here for free.
        [Fact]
        public void CorePurity_AllRules_RunOverInMemoryModel_WithoutFileAccess()
        {
            var model = InMemoryModel();

            Exception thrown = Record.Exception(() => InvariantEvaluator.Evaluate(model, InvariantRegistry.AllRules));

            Assert.Null(thrown);
        }

        // Guards the review gate (design "CitedContract-presence test"): reflection
        // over EVERY IRecordingInvariant implementation in the assembly asserts a
        // non-empty RuleId and CitedContract. Passes trivially with zero production
        // rules today and enforces the contract on every future rule - a rule that
        // ships without naming the production member it checks fails here.
        [Fact]
        public void EveryInvariantImplementation_DeclaresRuleIdAndCitedContract()
        {
            IEnumerable<Type> ruleTypes = SafeGetTypes(typeof(IRecordingInvariant).Assembly)
                .Where(t => t != null
                    && typeof(IRecordingInvariant).IsAssignableFrom(t)
                    && !t.IsInterface
                    && !t.IsAbstract
                    && t.GetConstructor(Type.EmptyTypes) != null);

            foreach (Type t in ruleTypes)
            {
                var rule = (IRecordingInvariant)Activator.CreateInstance(t);
                Assert.False(string.IsNullOrWhiteSpace(rule.RuleId),
                    $"{t.FullName} must declare a non-empty RuleId");
                Assert.False(string.IsNullOrWhiteSpace(rule.CitedContract),
                    $"{t.FullName} must declare a non-empty CitedContract (the production member it checks)");
            }
        }

        private static Type[] SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types;
            }
        }
    }
}
