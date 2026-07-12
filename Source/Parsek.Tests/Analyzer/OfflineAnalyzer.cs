using System;
using System.Collections.Generic;
using System.IO;

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
        /// directory, evaluate the production rule set, APPLY the per-save baseline
        /// per <paramref name="mode"/>, and write both report files into
        /// <paramref name="resultsDir"/>. Both path parameters are required so a
        /// caller can never scatter reports next to a user's save. Returns the report
        /// so a caller (the harness / a test) can read the counts.
        ///
        /// <paramref name="mode"/> defaults to <see cref="BaselineMode.Ignore"/> for
        /// source compatibility: an existing caller is byte-identical to the
        /// pre-feature analyzer (no baseline consulted; RED reduces to the old
        /// FAIL/STALE gate).
        /// </summary>
        internal static AnalysisReport Run(
            string saveDir, string resultsDir, Func<string, CelestialBody> bodyResolver,
            BaselineMode mode = BaselineMode.Ignore)
        {
            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, bodyResolver);
            AnalysisReport report = InvariantEvaluator.Evaluate(model);

            // Baseline lives beside the SAVE (not the results dir, which may be
            // redirected) so a triage copy carries it. The filter stays filesystem-
            // free: the orchestrator resolves presence + loads on Apply and hands the
            // pure filter the loaded baseline + fault list.
            string baselinePath = ResolveBaselinePath(saveDir);
            bool baselinePresent = !string.IsNullOrEmpty(baselinePath) && File.Exists(baselinePath);

            AnalysisBaseline baseline = null;
            IReadOnlyList<BaselineLoadFault> faults = null;
            if (mode == BaselineMode.Apply && baselinePresent)
            {
                (AnalysisBaseline loaded, List<BaselineLoadFault> loadFaults) = BaselineCodec.Load(baselinePath);
                baseline = loaded;
                faults = loadFaults;
            }

            BaselineFilter.Apply(report, baseline, mode, baselinePresent, faults);

            ReportWriter.Write(report, resultsDir);
            return report;
        }

        /// <summary>
        /// The only path that creates or updates a baseline (design "Authoring:
        /// -WriteBaseline"). Runs the analyzer in <see cref="BaselineMode.Ignore"/>
        /// (to see the TRUE, unfiltered findings and regenerate the reports), then
        /// builds + writes <c>&lt;save&gt;/analysis/baseline.cfg</c>, preserving human
        /// reasons on an existing baseline and pruning stale entries unless
        /// <paramref name="keepStale"/> retains them. Returns the written baseline.
        /// </summary>
        internal static AnalysisBaseline WriteBaselineForSave(
            string saveDir, string resultsDir, Func<string, CelestialBody> bodyResolver, bool keepStale)
        {
            AnalysisReport report = Run(saveDir, resultsDir, bodyResolver, BaselineMode.Ignore);

            string baselinePath = ResolveBaselinePath(saveDir);
            AnalysisBaseline existing = null;
            if (!string.IsNullOrEmpty(baselinePath) && File.Exists(baselinePath))
            {
                (AnalysisBaseline loaded, List<BaselineLoadFault> loadFaults) = BaselineCodec.Load(baselinePath);
                // A HARD fault (whole-file parse failure / future format version) on the
                // EXISTING baseline must refuse the write, not silently rewrite it. A
                // silent overwrite would destroy every human-authored reason in a file
                // the operator likely wants to fix (a hand-edit syntax error) rather
                // than regenerate. Fail loud with the fault detail; the operator fixes
                // or deletes the file deliberately.
                foreach (BaselineLoadFault fault in loadFaults)
                {
                    if (fault.IsHard)
                    {
                        ParsekLog.Warn("Analyzer",
                            "baseline write REFUSED save='" + (report.SaveName ?? "")
                            + "' existing baseline has hard fault kind=" + fault.Kind
                            + " detail=" + fault.Detail + " path='" + baselinePath + "'");
                        throw new InvalidOperationException(
                            "refusing to overwrite baseline '" + baselinePath
                            + "': existing file has a " + fault.Kind + " fault (" + fault.Detail
                            + "). Fix or delete it deliberately before re-authoring.");
                    }
                    // Soft entry fault (malformed / duplicate entry): proceed, but name
                    // it so the operator sees which entries were dropped on this update.
                    ParsekLog.Warn("Analyzer",
                        "baseline write proceeding despite soft fault in existing baseline kind="
                        + fault.Kind + " detail=" + fault.Detail);
                }
                existing = loaded;
            }

            return BaselineBuilder.BuildAndWrite(report, existing, keepStale, baselinePath);
        }

        /// <summary>
        /// Resolves the per-save baseline path: <c>&lt;save&gt;/analysis/baseline.cfg</c>.
        /// Beside the save (travels with a triage copy), never in a redirected results
        /// dir, and named so report regeneration (<c>&lt;leaf&gt;.analysis.*</c>) never
        /// clobbers it.
        /// </summary>
        internal static string ResolveBaselinePath(string saveDir)
        {
            if (string.IsNullOrEmpty(saveDir))
                return null;
            return Path.Combine(saveDir, "analysis", "baseline.cfg");
        }

        /// <summary>
        /// Parses the <c>PARSEK_ANALYZER_BASELINE_MODE</c> env value: <c>ignore</c> /
        /// <c>apply</c> / <c>forbid</c> (case-insensitive). Unset or unrecognized ->
        /// <see cref="BaselineMode.Ignore"/> (the safe default). Pure and testable.
        /// </summary>
        internal static BaselineMode ParseBaselineMode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return BaselineMode.Ignore;
            switch (value.Trim().ToLowerInvariant())
            {
                case "apply": return BaselineMode.Apply;
                case "forbid": return BaselineMode.Forbid;
                default: return BaselineMode.Ignore;
            }
        }
    }
}
