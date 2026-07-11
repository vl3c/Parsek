using System;

namespace Parsek.Tests.Analyzer
{
    // Offline analyzer orchestrator. Stays in Parsek.Tests because it drives the
    // Tests-only SaveDirectoryLoader + ReportWriter; the pure evaluate/registry
    // core it calls lives in Parsek.dll (Parsek.Analyzer.InvariantEvaluator /
    // InvariantRegistry, reachable via the GlobalUsings.cs global using).

    internal static class OfflineAnalyzer
    {
        /// <summary>
        /// End-to-end entry point shared by all offline run modes: load the save
        /// directory, evaluate the production rule set, and write both report files
        /// into <paramref name="resultsDir"/>. Both parameters are required so a
        /// caller can never scatter reports next to a user's save. Returns the report
        /// so a caller (the harness / a test) can read the counts.
        /// </summary>
        internal static AnalysisReport Run(
            string saveDir, string resultsDir, Func<string, CelestialBody> bodyResolver)
        {
            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, bodyResolver);
            AnalysisReport report = InvariantEvaluator.Evaluate(model);
            ReportWriter.Write(report, resultsDir);
            return report;
        }
    }
}
