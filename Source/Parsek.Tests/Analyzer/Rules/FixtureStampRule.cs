using System.Collections.Generic;
using System.Globalization;
using Parsek;

namespace Parsek.Tests.Analyzer.Rules
{
    // Fixture-generation stamp check (design doc "Fixture versioning", edge
    // case 22).
    //
    // When the analyzed subject is a fixture corpus (it carries a
    // fixture-generation.txt stamp the loader parsed into model.FixtureStamp) and
    // that stamp's generation differs from RecordingStore.CurrentRecordingSchemaGeneration,
    // EVERY recording in it is reported STALE-FIXTURE -- a DISTINCT verdict from
    // FAIL: the data is not wrong, the fixture corpus is out of date and needs the
    // M-A4 regeneration script re-run (a synthetic stamp) or a re-harvest (a
    // harvested stamp, which the no-migration policy cannot regenerate by script).
    // A STALE-FIXTURE run is red but reads as "regenerate fixtures", not "code
    // broke". A non-fixture subject (no stamp -> null) skips this check entirely.
    internal sealed class FixtureStampRule : IRecordingInvariant
    {
        internal const string RuleIdConst = "FIXTURE-STALE";

        public string RuleId => RuleIdConst;

        public string CitedContract => "RecordingStore.CurrentRecordingSchemaGeneration";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.FixtureStamp == null)
                return findings; // non-fixture subject -> skip

            FixtureStamp stamp = model.FixtureStamp.Value;
            int codeGen = RecordingStore.CurrentRecordingSchemaGeneration;
            if (stamp.SchemaGeneration == codeGen)
                return findings; // fixture is current

            string note = string.Equals(stamp.Provenance, "harvested", System.StringComparison.Ordinal)
                ? "re-harvest-queue"
                : "re-run-M-A4-regeneration";

            if (model.Recordings != null)
            {
                foreach (Recording rec in model.Recordings)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    findings.Add(new Finding(
                        RuleIdConst,
                        VerdictLevel.StaleFixture,
                        rec.RecordingId,
                        -1,
                        Inv("FIXTURE stale-fixture recording={0} stampGen={1} codeGen={2} provenance={3} action={4}",
                            rec.RecordingId, stamp.SchemaGeneration, codeGen, stamp.Provenance ?? "synthetic", note),
                        "RecordingStore.CurrentRecordingSchemaGeneration"));
                }
            }

            return findings;
        }

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
