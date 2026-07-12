using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek;

namespace Parsek.Analyzer.Rules
{
    // INV9 rewind-save + id validation (design doc "The invariant rules" INV9,
    // edge case 19).
    //
    // The field this rule checks is Recording.RewindSaveFileName -- the
    // Rewind-to-Launch quicksave captured at recording start. Production stores
    // it at Parsek/Saves/<id>.sfs (RecordingPaths.BuildRewindSaveRelativePath; see
    // FlightRecorder.CaptureRewindSave / CleanupOrphanedRewindSave and
    // RecordingStore.DeleteRecordingFiles, all of which resolve the file through
    // BuildRewindSaveRelativePath) with the fixed "parsek_rw_" filename prefix
    // (FlightRecorder.cs `$"parsek_rw_{shortId}"`; ParsekScenario's stale-sweep
    // globs "parsek_rw_*.sfs"). It is NOT the newer RewindPoint (rp_*) quicksave
    // system under Parsek/RewindPoints/, which is referenced by scenario
    // RewindPoints slots / BranchPoints that this offline model does not load.
    //
    // Verdicts:
    //  - A recording id or rewind id that fails RecordingPaths.ValidateRecordingId
    //    (path traversal / invalid chars) -> FAIL, emitted BEFORE any filesystem
    //    access so a `../evil` id never reaches the disk.
    //  - A referenced rewind save whose Parsek/Saves/<id>.sfs is MISSING -> severity
    //    splits by the recording's MergeState as a TRIAGE-SEVERITY heuristic, NOT a
    //    production contract. Rewindability of the parsek_rw_ save is itself
    //    MergeState-agnostic (GetRewindRecording / CanRewind / InitiateRewind never
    //    read MergeState), so the split does not claim "provisional = rewindable,
    //    sealed = not"; it grades how suspicious a dangler is by how recent / active
    //    the referencing slot is:
    //      * CommittedProvisional (a recent, still-active slot) -> FAIL
    //        (token "missing-rewind-save-provisional"). A dangling Rewind-to-Launch
    //        save on an open provisional slot is far likelier a live bug than the
    //        historical shared-delete residue tolerated on sealed rows. Narrow benign
    //        false-FAIL path: a provisional root whose shared rewind save was deleted
    //        by a sibling (the same benign mechanism described for the Immutable case
    //        below) can dangle without being a defect; baseline that finding with a
    //        human reason to recover.
    //      * Immutable / anything else -> WARN (token "missing-rewind-save"). A
    //        missing rewind save on a sealed recording is far likelier a dangling
    //        reference than proven corruption: RecordingStore.DeleteRecordingFiles
    //        deletes a rewind save with the recording being discarded WITHOUT
    //        reference-counting sibling recordings that share the same save via
    //        ParsekFlight.CopyRewindSaveToRoot ("first recorder wins"), so a
    //        surviving sibling can legitimately carry a now-deleted reference; and
    //        production treats a missing rewind hint as benign
    //        (ParsekScenario.ResolveLimboResumeRewindSave: "a missing hint is
    //        benign"). WARN surfaces the dangling reference without failing the run.
    //  - A referenced rewind save that exists but does NOT parse as a ConfigNode ->
    //    FAIL (a corrupt present file would break the rewind restore).
    //  - Unreferenced parsek_rw_*.sfs files on disk are an EXPECTED benign state
    //    (ParsekFlight.cs: "keep the on-disk parsek_rw_*.sfs but no recording ever
    //    references it"), so they are reported as a single per-save INFO inventory
    //    line (count), never a WARN/FAIL.
    //
    // File-scoped: existence / orphan checks read model.SaveDirectory. Validation
    // FAILs are emitted regardless of SaveDirectory (they touch no file); the
    // existence / orphan checks no-op when SaveDirectory is null (a purely
    // in-memory model / the core-purity test). Never throws.
    internal sealed class Inv9RewindPoint : IRecordingInvariant
    {
        internal const string RuleIdConst = "INV9-REWINDPOINT";

        // Production rewind-save filename prefix (FlightRecorder.cs
        // `$"parsek_rw_{shortId}"`), used to scope the orphan inventory scan to
        // genuine rewind saves and skip other Parsek/Saves/ entries such as
        // parsek_career_start.sfs.
        internal const string RewindSavePrefix = "parsek_rw_";

        // Parsek/Saves/ is the durable home of RewindSaveFileName quicksaves
        // (RecordingPaths.BuildRewindSaveRelativePath -> Path.Combine("Parsek",
        // "Saves", ...)). Mirror the leading segment here for the orphan directory
        // scan; the per-file existence check still routes through the production
        // path builder.
        private const string SavesSubdir = "Parsek/Saves";

        public string RuleId => RuleIdConst;

        public string CitedContract =>
            "RecordingPaths.ValidateRecordingId / RecordingPaths.BuildRewindSaveRelativePath";

        public IEnumerable<Finding> Evaluate(AnalyzerModel model)
        {
            var findings = new List<Finding>();
            if (model?.Recordings == null)
                return findings;

            var referencedRewindIds = new HashSet<string>(StringComparer.Ordinal);

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

                string rwId = rec.RewindSaveFileName;
                if (string.IsNullOrEmpty(rwId))
                    continue;

                // Validate the rewind-save id BEFORE touching the filesystem.
                if (!RecordingPaths.ValidateRecordingId(rwId, RecordingIdValidationLogContext.Test))
                {
                    findings.Add(Fail(rec.RecordingId ?? rwId, -1,
                        Inv("INV9 badid recording={0} field=RewindSaveFileName rewindId={1}",
                            rec.RecordingId ?? "<none>", rwId)));
                    continue;
                }
                referencedRewindIds.Add(rwId);

                if (string.IsNullOrEmpty(model.SaveDirectory))
                    continue;

                // RewindSaveFileName lives at Parsek/Saves/<id>.sfs, NOT under
                // Parsek/RewindPoints/. Route through the production path builder.
                string rel = RecordingPaths.BuildRewindSaveRelativePath(rwId);
                if (rel == null)
                    continue; // validated above; defensive
                string rwPath = Path.Combine(model.SaveDirectory, rel);

                if (!File.Exists(rwPath))
                {
                    // Dangling reference. Severity splits by MergeState as a triage
                    // heuristic (see class comment): a recent / still-active
                    // CommittedProvisional recording whose own rewind save is gone ->
                    // FAIL; a sealed (Immutable) or any other recording -> WARN.
                    if (rec.MergeState == MergeState.CommittedProvisional)
                    {
                        findings.Add(FailFile(rec.RecordingId ?? rwId,
                            Inv("INV9 missing-rewind-save-provisional recording={0} rewindId={1}",
                                rec.RecordingId ?? "<none>", rwId)));
                    }
                    else
                    {
                        findings.Add(Warn(rec.RecordingId ?? rwId,
                            Inv("INV9 missing-rewind-save recording={0} rewindId={1}",
                                rec.RecordingId ?? "<none>", rwId)));
                    }
                    continue;
                }

                if (!ParsesAsConfigNode(rwPath))
                {
                    findings.Add(Fail(rec.RecordingId ?? rwId, -1,
                        Inv("INV9 unparsable-rewind-save recording={0} rewindId={1}",
                            rec.RecordingId ?? "<none>", rwId)));
                }
            }

            InventoryOrphanRewindSaves(model, referencedRewindIds, findings);
            return findings;
        }

        private static void InventoryOrphanRewindSaves(
            AnalyzerModel model, HashSet<string> referencedRewindIds, List<Finding> findings)
        {
            if (string.IsNullOrEmpty(model.SaveDirectory))
                return;

            string savesDir = Path.Combine(model.SaveDirectory, SavesSubdir);
            if (!Directory.Exists(savesDir))
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(savesDir, RewindSavePrefix + "*.sfs");
            }
            catch
            {
                return;
            }

            int orphanCount = 0;
            foreach (string file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(id) || referencedRewindIds.Contains(id))
                    continue;
                orphanCount++;
            }

            // Unreferenced parsek_rw_*.sfs files are an expected benign state
            // (ParsekFlight.cs). Emit one inventory INFO per save when any exist;
            // stay silent (clean-data-is-green) when none do.
            if (orphanCount > 0)
            {
                findings.Add(new Finding(RuleIdConst, VerdictLevel.Info,
                    model.SaveName ?? "<save>", -1,
                    Inv("INV9 orphan-rewind-saves count={0} (unreferenced parsek_rw_*; expected per ParsekFlight)",
                        orphanCount),
                    "RecordingPaths.BuildRewindSaveRelativePath"));
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

        private static Finding Warn(string target, string message) =>
            new Finding(RuleIdConst, VerdictLevel.Warn, target, -1, message,
                "RecordingPaths.BuildRewindSaveRelativePath");

        // FAIL for a file-existence anomaly (missing rewind save on an open
        // provisional slot). Cites the path builder rather than the id validator
        // because the contract at stake is the on-disk rewind-save location, not id
        // validity.
        private static Finding FailFile(string target, string message) =>
            new Finding(RuleIdConst, VerdictLevel.Fail, target, -1, message,
                "RecordingPaths.BuildRewindSaveRelativePath");

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
