using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV9 rewind-point + id validation (design doc "The invariant rules" INV9,
    // edge case 19).
    //
    // Every recording id and every referenced RewindPoint id must pass
    // RecordingPaths.ValidateRecordingId BEFORE any filesystem access, so a
    // path-traversal id (../evil) is rejected by validation and never reaches the
    // filesystem (a security regression if it did). A referenced RewindPoint's
    // Parsek/RewindPoints/<id>.sfs must exist and parse as a ConfigNode, else
    // FAIL; an RP quicksave on disk that no recording references -> WARN (orphan).
    //
    // File-scoped: existence / parse checks read model.SaveDirectory. Validation
    // FAILs are emitted regardless of SaveDirectory (they touch no file); the
    // existence / orphan checks no-op when SaveDirectory is null (a purely
    // in-memory model / the core-purity test). Never throws.
    internal sealed class Inv9RewindPoint : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV9-REWINDPOINT";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "RecordingPaths.ValidateRecordingId / RecordingPaths.BuildRewindPointRelativePath";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            var referencedRpIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null)
                    continue;

                // Every recording id must validate (traversal / invalid chars).
                if (!string.IsNullOrEmpty(rec.RecordingId)
                    && !RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test))
                {
                    findings.Add(Fail(rec.RecordingId, -1,
                        Inv("INV9 badid recording={0} field=RecordingId", rec.RecordingId)));
                }

                string rpId = rec.RewindSaveFileName;
                if (string.IsNullOrEmpty(rpId))
                    continue;

                // Validate the RewindPoint id BEFORE touching the filesystem.
                if (!RecordingPaths.ValidateRecordingId(rpId, RecordingIdValidationLogContext.Test))
                {
                    findings.Add(Fail(rec.RecordingId ?? rpId, -1,
                        Inv("INV9 badid recording={0} field=RewindSaveFileName rewindId={1}",
                            rec.RecordingId ?? "<none>", rpId)));
                    continue;
                }
                referencedRpIds.Add(rpId);

                if (string.IsNullOrEmpty(model.SaveDirectory))
                    continue;

                string rel = RecordingPaths.BuildRewindPointRelativePath(rpId);
                if (rel == null)
                    continue; // validated above; defensive
                string rpPath = Path.Combine(model.SaveDirectory, rel);

                if (!File.Exists(rpPath))
                {
                    findings.Add(Fail(rec.RecordingId ?? rpId, -1,
                        Inv("INV9 missing-rewindpoint recording={0} rewindId={1}",
                            rec.RecordingId ?? "<none>", rpId)));
                    continue;
                }

                if (!ParsesAsConfigNode(rpPath))
                {
                    findings.Add(Fail(rec.RecordingId ?? rpId, -1,
                        Inv("INV9 unparsable-rewindpoint recording={0} rewindId={1}",
                            rec.RecordingId ?? "<none>", rpId)));
                }
            }

            InventoryOrphanRewindPoints(model, referencedRpIds, findings);
            return findings;
        }

        private static void InventoryOrphanRewindPoints(
            AnalyzerModel model, HashSet<string> referencedRpIds, List<Finding> findings)
        {
            if (string.IsNullOrEmpty(model.SaveDirectory))
                return;

            string rpDir = Path.Combine(model.SaveDirectory, RecordingPaths.RewindPointsSubdir);
            if (!Directory.Exists(rpDir))
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(rpDir, "*.sfs");
            }
            catch
            {
                return;
            }

            foreach (string file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(id) || referencedRpIds.Contains(id))
                    continue;
                findings.Add(new Finding(RuleIdConst, VerdictLevel.Warn, id, -1,
                    Inv("INV9 orphan-rewindpoint rewindId={0}", id),
                    "RecordingPaths.BuildRewindPointRelativePath"));
            }
        }

        private static bool ParsesAsConfigNode(string path)
        {
            try
            {
                return ConfigNode.Load(path) != null;
            }
            catch
            {
                return false;
            }
        }

        private static Finding Fail(string target, int sectionIndex, string message) =>
            new Finding(RuleIdConst, VerdictLevel.Fail, target, sectionIndex, message,
                "RecordingPaths.ValidateRecordingId");

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
