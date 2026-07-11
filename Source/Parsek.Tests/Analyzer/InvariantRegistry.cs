using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Analyzer.Rules;

namespace Parsek.Tests.Analyzer
{
    // The invariant registry and the pure Evaluate entry point (task 0.3).
    //
    // Phase 0/1 ships the registry with an EMPTY rule set: the rules land in
    // Phase 2+ under Analyzer/Rules/. The framework (Evaluate + verdict policy +
    // the CitedContract-presence and core-purity gates) is proven now so every
    // future rule slots in without reworking the harness.

    internal static class InvariantRegistry
    {
        /// <summary>
        /// The production rule set, in registration order. Concrete rules are
        /// added here (and as Analyzer/Rules/*.cs files) from Phase 2 onward.
        /// </summary>
        internal static IReadOnlyList<IRecordingInvariant> AllRules { get; } = BuildRules();

        private static IReadOnlyList<IRecordingInvariant> BuildRules()
        {
            var rules = new List<IRecordingInvariant>
            {
                new LoadFaultRule(),
                new Inv1UtMonotonic(),
                new Inv2NoDoubleCover(),
                new Inv3RelativeContract(),
                new Inv7TreeTopology(),
                new Inv5SchemaGate(),
                new Inv4PartEventPid(),
                new Inv6ResourceManifest(),
                new Inv10CodecRoundtrip(),
                new Inv7bAnnotationStale(),
                new Inv9RewindPoint(),
                new Inv8Ledger(),
                new FixtureStampRule(),
            };

            // Diagnostic logging (design "Diagnostic Logging"): every rule logs its
            // CitedContract once at registration so the contract citations are
            // visible in a run log, not just in source.
            foreach (IRecordingInvariant rule in rules)
            {
                ParsekLog.Verbose("Analyzer",
                    "rule " + rule.RuleId + " cites " + rule.CitedContract);
            }

            return rules;
        }
    }

    internal static class Analyzer
    {
        /// <summary>
        /// End-to-end entry point shared by all run modes (design "Run modes"):
        /// load the save directory, evaluate the production rule set, and write both
        /// report files into <paramref name="resultsDir"/>. Both parameters are
        /// required so a caller can never scatter reports next to a user's save.
        /// Returns the report so a caller (the harness / a test) can read the counts.
        /// </summary>
        internal static AnalysisReport Run(
            string saveDir, string resultsDir, Func<string, CelestialBody> bodyResolver)
        {
            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, bodyResolver);
            AnalysisReport report = Evaluate(model);
            ReportWriter.Write(report, resultsDir);
            return report;
        }

        /// <summary>Evaluates the production rule set over the loaded model.</summary>
        internal static AnalysisReport Evaluate(AnalyzerModel model)
        {
            return Evaluate(model, InvariantRegistry.AllRules);
        }

        /// <summary>
        /// Pure core: runs the given rules over the model and aggregates findings
        /// into a report. Never touches a file (the loader already ran); this is the
        /// exact code path the future in-game H5 category reuses.
        /// </summary>
        internal static AnalysisReport Evaluate(AnalyzerModel model, IReadOnlyList<IRecordingInvariant> rules)
        {
            var report = new AnalysisReport
            {
                SaveName = model?.SaveName,
                SubjectSchemaGeneration = DiscoverSubjectSchemaGeneration(model),
            };

            if (rules != null && model != null)
            {
                foreach (IRecordingInvariant rule in rules)
                {
                    if (rule == null)
                        continue;
                    IEnumerable<Finding> findings = rule.Evaluate(model);
                    if (findings != null)
                        report.Findings.AddRange(findings);
                }
            }

            report.Counts = Counts.From(report.Findings);
            return report;
        }

        /// <summary>
        /// Discovers the subject's schema generation from the loaded data: the
        /// highest <see cref="Recording.RecordingSchemaGeneration"/> across loaded
        /// recordings, or 0 when the save has no recordings.
        /// </summary>
        internal static int DiscoverSubjectSchemaGeneration(AnalyzerModel model)
        {
            int gen = 0;
            if (model?.Recordings != null)
            {
                foreach (Recording rec in model.Recordings)
                {
                    if (rec != null && rec.RecordingSchemaGeneration > gen)
                        gen = rec.RecordingSchemaGeneration;
                }
            }
            return gen;
        }
    }
}
