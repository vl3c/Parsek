using System.Collections.Generic;
using Parsek.Tests.Analyzer.Rules;

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
        internal static IReadOnlyList<IRecordingInvariant> AllRules { get; } =
            new List<IRecordingInvariant>
            {
                new Inv2NoDoubleCover(),
                new Inv7TreeTopology(),
            };
    }

    internal static class Analyzer
    {
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
