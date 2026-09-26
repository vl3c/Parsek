using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the automation-only <c>SafeWriteCrash</c> verb; the
    /// phase contract is on <see cref="TestCommandSafeWriteCrash"/>. SINGLE-PHASE: every phase
    /// finishes inside the call. The baseline is static so it survives the cold reload
    /// between <c>arm</c> and the final <c>probe</c>.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private static string safeWriteCrashRecordingId;
        private static string safeWriteCrashFinalPath;
        private static string safeWriteCrashBaselineDigest;
        private static int safeWriteCrashBaselinePoints = -1;

        private void SafeWriteCrashImpl(ParsedCommand cmd)
        {
            string phase = ArgOrNull(cmd, TestCommandSafeWriteCrash.PhaseKey);
            string reason = TestCommandSafeWriteCrash.ValidatePhase(phase);
            if (reason != null)
            {
                RejectSafeWriteCrash(reason, $"phase={phase ?? string.Empty}");
                return;
            }

            if (phase == TestCommandSafeWriteCrash.PhaseColdReload)
            {
                var scenario = UnityEngine.Object.FindObjectOfType(typeof(ParsekScenario)) as ParsekScenario;
                ParsekLog.Info(Tag, $"safewritecrash coldreload prepare scenarioPresent={Bool(scenario != null)}");
                ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore(
                    scenario != null
                        ? (Action)scenario.UnsubscribeStateRecorderForIsolatedBatchFlightBaselineRestore
                        : null);
                SetExecResult("OK", Payload(Kv("prepared", "true")), null);
                return;
            }

            if (phase == TestCommandSafeWriteCrash.PhaseProbe)
            {
                if (safeWriteCrashRecordingId == null)
                {
                    RejectSafeWriteCrash("safewritecrash-no-baseline", "probe before arm");
                    return;
                }
                string id = safeWriteCrashRecordingId;
                string digest = DigestOrNone(safeWriteCrashFinalPath);
                int residue = 0;
                string dir = Path.GetDirectoryName(safeWriteCrashFinalPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var names = new List<string>();
                    foreach (string f in Directory.GetFiles(dir))
                        names.Add(Path.GetFileName(f));
                    residue = TestCommandSafeWriteCrash.CountResidue(names, id);
                }
                Recording loaded = FindCommittedTreeRecording(id);
                int points = loaded != null ? loaded.Points.Count : -1;
                bool unchanged = digest == safeWriteCrashBaselineDigest;
                ParsekLog.Info(Tag, TestCommandSafeWriteCrash.FormatProbeLine(
                    id, FileIOUtils.CrashAfterTempFiredCount, FileIOUtils.IsCrashAfterTempArmed,
                    digest, unchanged, residue, points, safeWriteCrashBaselinePoints,
                    loaded != null, loaded != null && loaded.SidecarLoadFailed));
                SetExecResult("OK", Payload(
                    Kv("fired", Int(FileIOUtils.CrashAfterTempFiredCount)),
                    Kv("unchanged", Bool(unchanged)),
                    Kv("residue", Int(residue)),
                    Kv("points", Int(points))), null);
                return;
            }

            // phase=arm
            string recId = ArgOrNull(cmd, TestCommandSafeWriteCrash.RecordingKey);
            if (string.IsNullOrEmpty(recId))
            {
                RejectSafeWriteCrash("safewritecrash-recording-arg-missing", "arm needs recording=");
                return;
            }
            if (FileIOUtils.IsCrashAfterTempArmed)
            {
                RejectSafeWriteCrash("safewritecrash-already-armed", $"recording={recId}");
                return;
            }
            Recording rec = FindCommittedTreeRecording(recId);
            if (rec == null)
            {
                RejectSafeWriteCrash("safewritecrash-recording-not-found", $"recording={recId}");
                return;
            }
            string finalPath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildTrajectoryRelativePath(recId));
            if (string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
            {
                RejectSafeWriteCrash("safewritecrash-no-sidecar", $"recording={recId} path={finalPath ?? "<none>"}");
                return;
            }
            string pattern = TestCommandSafeWriteCrash.BuildPattern(recId);
            if (!FileIOUtils.ArmCrashAfterTemp(pattern, armed))
            {
                RejectSafeWriteCrash("safewritecrash-arm-refused", $"recording={recId}");
                return;
            }
            safeWriteCrashRecordingId = recId;
            safeWriteCrashFinalPath = finalPath;
            safeWriteCrashBaselineDigest = DigestOrNone(finalPath);
            safeWriteCrashBaselinePoints = rec.Points.Count;
            // The next save must rewrite this sidecar for the hook to have a write to fire on.
            rec.MarkFilesDirty();
            ParsekLog.Info(Tag, TestCommandSafeWriteCrash.FormatArmLine(
                recId, pattern, safeWriteCrashBaselineDigest, safeWriteCrashBaselinePoints));
            SetExecResult("OK", Payload(
                Kv("armed", "true"),
                Kv("recording", recId),
                Kv("baselineDigest", safeWriteCrashBaselineDigest),
                Kv("baselinePoints", Int(safeWriteCrashBaselinePoints))), null);
        }

        private void RejectSafeWriteCrash(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"safewritecrash rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }

        private static Recording FindCommittedTreeRecording(string recordingId)
        {
            var trees = RecordingStore.CommittedTrees;
            for (int t = 0; trees != null && t < trees.Count; t++)
            {
                if (trees[t]?.Recordings != null
                    && trees[t].Recordings.TryGetValue(recordingId, out Recording rec) && rec != null)
                    return rec;
            }
            return null;
        }

        private static string DigestOrNone(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return TestCommandSafeWriteCrash.ShortHex(null);
            using (var sha = SHA256.Create())
                return TestCommandSafeWriteCrash.ShortHex(sha.ComputeHash(File.ReadAllBytes(path)));
        }
    }
}
