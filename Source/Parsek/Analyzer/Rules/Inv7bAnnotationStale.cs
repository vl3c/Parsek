using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek;

namespace Parsek.Analyzer.Rules
{
    // INV7b annotation staleness (design doc "The invariant rules" INV7b, edge
    // case 15).
    //
    // A .pann pipeline-annotation sidecar is a DERIVED smoothing cache, not
    // authored data (rendering design 17.3.1). Where one exists, its recorded
    // source epoch (PannotationsSidecarBinary.TryProbe -> SourceSidecarEpoch) must
    // not be older than the paired .prec's current epoch
    // (TrajectorySidecarBinary.TryProbe -> SidecarEpoch): an older annotation was
    // built against a superseded trajectory and would render a ghost from a stale
    // cache -> WARN. A .pann with no paired .prec is an orphan cache -> WARN.
    //
    // File-scoped: this rule probes the two sidecars directly, so it reads
    // model.SaveDirectory (the plan makes INV7b a loader-scoped rule). When
    // SaveDirectory is null (a purely in-memory model / the core-purity test), it
    // no-ops without touching a file. It never throws (probe failures are contained).
    internal sealed class Inv7bAnnotationStale : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV7B-ANNOTATION-STALE";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "PannotationsSidecarBinary.TryProbe / RecordingPaths.BuildAnnotationsRelativePath";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model == null || string.IsNullOrEmpty(model.SaveDirectory))
                return findings;

            string recordingsDir = Path.Combine(model.SaveDirectory, "Parsek", "Recordings");
            if (!Directory.Exists(recordingsDir))
                return findings;

            string[] pannFiles;
            try
            {
                pannFiles = Directory.GetFiles(recordingsDir, "*.pann");
            }
            catch
            {
                return findings;
            }

            foreach (string pannFile in pannFiles)
            {
                string id = Path.GetFileNameWithoutExtension(pannFile);
                if (string.IsNullOrEmpty(id))
                    continue;

                if (!PannotationsSidecarBinary.TryProbe(pannFile, out PannotationsSidecarProbe pannProbe))
                {
                    // A .pann we cannot probe is a corrupt derived cache; surface it
                    // as stale/regenerable, not a hard failure.
                    findings.Add(Warn(id, -1,
                        Inv("INV7b annotation recording={0} error=probe-failed reason={1}",
                            id, pannProbe.FailureReason ?? "unknown")));
                    continue;
                }

                string precPath = Path.Combine(model.SaveDirectory, RecordingPaths.BuildTrajectoryRelativePath(id));
                if (!File.Exists(precPath))
                {
                    findings.Add(Warn(id, -1,
                        Inv("INV7b annotation recording={0} error=orphan pannEpoch={1}",
                            id, pannProbe.SourceSidecarEpoch)));
                    continue;
                }

                if (!TrajectorySidecarBinary.TryProbe(precPath, out TrajectorySidecarProbe precProbe))
                    continue; // the .prec problem is INV5's to report, not INV7b's.

                if (pannProbe.SourceSidecarEpoch < precProbe.SidecarEpoch)
                {
                    findings.Add(Warn(id, -1,
                        Inv("INV7b annotation recording={0} error=stale pannEpoch={1} precEpoch={2}",
                            id, pannProbe.SourceSidecarEpoch, precProbe.SidecarEpoch)));
                }
            }

            return findings;
        }

        private static Finding Warn(string target, int sectionIndex, string message) =>
            new Finding(RuleIdConst, VerdictLevel.Warn, target, sectionIndex, message,
                "PannotationsSidecarBinary.TryProbe");

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
