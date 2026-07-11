using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV5 schema gate. All fixtures are pure in-memory AnalyzerModels (plan
    // fixture strategy: INV5-metadata is pure-model; the sidecar / orphan /
    // load-fault side-tables the rule reads are populated directly here, which
    // exercises the pure rule without the loader). Each test names the regression
    // it guards.
    public class Inv5SchemaGateTests
    {
        private static AnalyzerModel ModelWith(
            IEnumerable<Recording> recordings = null,
            IReadOnlyDictionary<string, (int, int)> sidecarSchema = null,
            IEnumerable<LoadFault> loadFaults = null)
        {
            return new AnalyzerModel
            {
                SaveName = "inv5",
                Recordings = (recordings ?? Enumerable.Empty<Recording>()).ToList(),
                SidecarSchema = sidecarSchema != null
                    ? sidecarSchema.ToDictionary(k => k.Key, v => v.Value)
                    : new Dictionary<string, (int, int)>(),
                LoadFaults = (loadFaults ?? Enumerable.Empty<LoadFault>()).ToList(),
            };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv5SchemaGate().Evaluate(model).ToList();
        }

        private static Recording Rec(string id, int generation, int formatVersion = 1)
        {
            return new Recording
            {
                RecordingId = id,
                RecordingSchemaGeneration = generation,
                RecordingFormatVersion = formatVersion,
            };
        }

        // Guards: a current-generation recording (gen 4, format 1) produces zero
        // INV5 findings. Fails if the gate rejects valid current data.
        [Fact]
        public void CurrentGeneration_NoFindings()
        {
            var model = ModelWith(new[]
            {
                Rec("ok0", RecordingStore.CurrentRecordingSchemaGeneration,
                    RecordingStore.CurrentRecordingFormatVersion),
            });

            Assert.Empty(Run(model));
        }

        // Guards: each metadata gate reason maps to the exact production reason
        // string, so a triage grep matches KSP.log. Fails if the analyzer's reasons
        // drift from RecordingStore.IsRecordingSchemaCompatible.
        [Theory]
        [InlineData(0, 1, "generation-missing")]
        [InlineData(3, 1, "generation-older")]
        [InlineData(5, 1, "generation-newer")]
        [InlineData(4, 2, "format-version-mismatch")]
        public void MetadataGate_EachReason_Fails(int generation, int formatVersion, string reason)
        {
            var model = ModelWith(new[] { Rec("bad0", generation, formatVersion) });

            List<Finding> findings = Run(model);

            Assert.Contains(findings, f =>
                f.RuleId == Inv5SchemaGate.SchemaGateRuleId
                && f.Level == VerdictLevel.Fail
                && f.Target == "bad0"
                && f.Message.Contains("reason=" + reason));
        }

        // Guards (edge case 4): metadata passes the gate but the paired sidecar
        // reports a different generation -> generation-mismatch FAIL. Fails if a
        // drifted sidecar schema silently passes.
        [Fact]
        public void SidecarGenerationDisagreement_Fails()
        {
            var model = ModelWith(
                new[] { Rec("rec0", 4, 1) },
                new Dictionary<string, (int, int)> { ["rec0"] = (3, 1) });

            List<Finding> findings = Run(model);

            Assert.Contains(findings, f =>
                f.RuleId == Inv5SchemaGate.SchemaGateRuleId
                && f.Level == VerdictLevel.Fail
                && f.Target == "rec0"
                && f.Message.Contains("reason=generation-mismatch")
                && f.Message.Contains("sidecarGen=3"));
        }

        // Guards (edge case 20): a SidecarSchema key with no matching tree recording
        // is a stray .prec -> WARN, not FAIL. Fails if orphan sidecars are ignored
        // or escalated to FAIL.
        [Fact]
        public void OrphanSidecar_Warns()
        {
            var model = ModelWith(
                new[] { Rec("ref0", 4, 1) },
                new Dictionary<string, (int, int)>
                {
                    ["ref0"] = (4, 1),
                    ["orphan0"] = (4, 1),
                });

            List<Finding> findings = Run(model);

            Finding warn = Assert.Single(findings, f => f.RuleId == Inv5SchemaGate.OrphanSidecarRuleId);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.Equal("orphan0", warn.Target);
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }

        // Guards (edge cases 1-3): a trajectory LoadFault (truncated / text /
        // pre-reset) surfaces as a FAIL carrying the exact production reason string.
        // Fails if malformed sidecars are dropped instead of reported.
        [Fact]
        public void TrajectoryLoadFault_FailsWithReason()
        {
            var model = ModelWith(
                loadFaults: new[]
                {
                    new LoadFault("p", "trajectory", "text-sidecar-unsupported", "text0"),
                });

            List<Finding> findings = Run(model);

            Assert.Contains(findings, f =>
                f.RuleId == Inv5SchemaGate.SchemaGateRuleId
                && f.Level == VerdictLevel.Fail
                && f.Target == "text0"
                && f.Message.Contains("reason=text-sidecar-unsupported"));
        }

        // Guards: a sidecar that both load-faulted AND disagrees on generation is
        // reported once (via the load fault), not double-counted with a spurious
        // generation-mismatch. Fails if the two paths both fire for one bad sidecar.
        [Fact]
        public void SidecarLoadFault_DoesNotAlsoEmitGenerationMismatch()
        {
            var model = ModelWith(
                new[] { Rec("t0", 4, 1) },
                new Dictionary<string, (int, int)> { ["t0"] = (3, 1) },
                new[] { new LoadFault("p", "trajectory", "magic-mismatch", "t0") });

            List<Finding> findings = Run(model);

            Assert.DoesNotContain(findings, f => f.Message.Contains("reason=generation-mismatch"));
            Assert.Contains(findings, f => f.Message.Contains("reason=magic-mismatch"));
        }

        // Guards: the generations inventory INFO fires only when a non-current
        // generation is present (informative), keeping a homogeneous current-gen
        // save finding-free. Fails if the inventory INFO leaks onto clean data.
        [Fact]
        public void GenerationsInventory_InfoOnlyWhenNonCurrentPresent()
        {
            var clean = ModelWith(new[] { Rec("ok0", 4, 1) });
            Assert.DoesNotContain(Run(clean), f => f.RuleId == Inv5SchemaGate.GenerationsRuleId);

            var mixed = ModelWith(new[] { Rec("ok0", 4, 1), Rec("old0", 3, 1) });
            Finding info = Assert.Single(Run(mixed), f => f.RuleId == Inv5SchemaGate.GenerationsRuleId);
            Assert.Equal(VerdictLevel.Info, info.Level);
            Assert.Contains("generations=3,4", info.Message);
        }
    }
}
