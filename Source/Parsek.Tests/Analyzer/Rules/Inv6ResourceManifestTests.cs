using System.Collections.Generic;
using System.Linq;
using Parsek;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV6 resource manifest consistency. Pure in-memory model; the manifest
    // round-trips through the codec into a scratch ConfigNode (no file). Each test
    // names the regression it guards.
    public class Inv6ResourceManifestTests
    {
        private static AnalyzerModel ModelWith(params Recording[] recs)
        {
            return new AnalyzerModel { SaveName = "inv6", Recordings = recs.ToList() };
        }

        private static List<Finding> Run(AnalyzerModel model)
        {
            return new Inv6ResourceManifest().Evaluate(model).ToList();
        }

        // Guards: a well-formed manifest round-trips -> zero INV6 findings. Fails if
        // a valid manifest is flagged (codec / comparison over-strict).
        [Fact]
        public void ManifestPresent_RoundTrips_NoFindings()
        {
            var rec = new Recording
            {
                RecordingId = "ok0",
                StartResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 100.0, maxAmount = 100.0 },
                    ["Oxidizer"] = new ResourceAmount { amount = 122.0, maxAmount = 122.0 },
                },
                EndResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 5.0, maxAmount = 100.0 },
                    ["Oxidizer"] = new ResourceAmount { amount = 6.0, maxAmount = 122.0 },
                },
            };

            Assert.Empty(Run(ModelWith(rec)));
        }

        // Guards (edge case 9): a recording with no manifest (Gloops / showcase) ->
        // zero findings. Fails if the rule wrongly requires manifests on ghost-only
        // recordings.
        [Fact]
        public void ManifestAbsent_NoFindings()
        {
            var rec = new Recording { RecordingId = "nomanifest0" };
            Assert.Empty(Run(ModelWith(rec)));
        }

        // Guards: a manifest whose round-trip loses a resource (an empty-name entry
        // the codec drops on deserialize) -> INV6 FAIL naming the resource. Fails if
        // the round-trip comparison is too shallow to notice a dropped resource.
        [Fact]
        public void ManifestRoundTripDropsResource_Fails()
        {
            var rec = new Recording
            {
                RecordingId = "bad0",
                StartResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 100.0, maxAmount = 100.0 },
                    // Empty-name resource: SerializeResourceManifest writes name="",
                    // DeserializeResourceManifest skips empty names -> the key vanishes.
                    [""] = new ResourceAmount { amount = 3.0, maxAmount = 3.0 },
                },
            };

            List<Finding> findings = Run(ModelWith(rec));

            Assert.Contains(findings, f =>
                f.RuleId == Inv6ResourceManifest.RuleIdConst
                && f.Level == VerdictLevel.Fail
                && f.Target == "bad0");
        }
    }
}
