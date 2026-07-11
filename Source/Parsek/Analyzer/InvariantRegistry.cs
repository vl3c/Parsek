using System.Collections.Generic;
using Parsek.Analyzer.Rules;

namespace Parsek.Analyzer
{
    // The invariant registry and the pure evaluator entry point.
    //
    // Both live in Parsek.dll (module M-A3 moved the pure core here) so the in-game
    // H5 RecordingInvariants category can reuse the exact same rule set and verdict
    // policy the offline analyzer uses. The offline orchestrator that loads a save
    // directory and writes report files (OfflineAnalyzer.Run) stays in Parsek.Tests
    // because it depends on the Tests-only loader/writer; this file is pure.

    internal static class InvariantRegistry
    {
        /// <summary>
        /// The production rule set, in registration order.
        /// </summary>
        internal static IReadOnlyList<IRecordingInvariant> AllRules { get; } = BuildRules();

        /// <summary>
        /// The pure-core subset the in-game H5 <c>RecordingInvariants</c> category runs
        /// (module M-A3, design "H5 - RecordingInvariants in-game category"): INV1-INV8,
        /// the rules that are pure over an <see cref="AnalyzerModel"/> with no
        /// loader-supplied inputs. The loader-scoped rules (LoadFaultRule, INV7b, INV9,
        /// INV10) depend on on-disk sidecar / rewind-point files + LoadFault data the
        /// in-game builder does not populate, and FixtureStampRule is unreachable (the
        /// in-game model carries a null FixtureStamp); excluding them keeps the in-game
        /// findings identical to what the offline pure core emits over the same
        /// live-sourced model. Because the set is shared, adding a pure-core invariant to
        /// M-A1 automatically strengthens H5 with no M-A3 change.
        /// </summary>
        internal static IReadOnlyList<IRecordingInvariant> InGamePureCoreRules { get; } =
            new List<IRecordingInvariant>
            {
                new Inv1UtMonotonic(),
                new Inv2NoDoubleCover(),
                new Inv3RelativeContract(),
                new Inv4PartEventPid(),
                new Inv5SchemaGate(),
                new Inv6ResourceManifest(),
                new Inv7TreeTopology(),
                new Inv8Ledger(),
            };

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

    /// <summary>
    /// Pure evaluator: runs the invariant rule set over an already-built
    /// <see cref="AnalyzerModel"/> and aggregates the findings into a report. Never
    /// touches a file (the model builder already ran), so this is the exact code
    /// path both the offline analyzer and the in-game H5 category reuse.
    /// </summary>
    internal static class InvariantEvaluator
    {
        /// <summary>Evaluates the production rule set over the loaded model.</summary>
        internal static AnalysisReport Evaluate(AnalyzerModel model)
        {
            return Evaluate(model, InvariantRegistry.AllRules);
        }

        /// <summary>
        /// Pure core: runs the given rules over the model and aggregates findings
        /// into a report. Never touches a file.
        /// </summary>
        internal static AnalysisReport Evaluate(AnalyzerModel model, IReadOnlyList<IRecordingInvariant> rules)
        {
            var report = new AnalysisReport
            {
                SaveName = model?.SaveName,
                SubjectSchemaGeneration = DiscoverSubjectSchemaGeneration(model),
                // Runtime-only signal for BaselineFilter's stamped-fixture refusal;
                // not serialized, so it does not touch the report schema.
                SubjectIsStampedFixture = model?.FixtureStamp != null,
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
