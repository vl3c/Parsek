using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek.InGameTests
{
    /// <summary>
    /// D16 storage cells driven against the LIVE save: recording-id path validation on
    /// the ids the save actually carries (`path-validation`), the readable `.txt`
    /// sidecar mirrors written and removed by the product's own reconcile pass
    /// (`txt-mirrors`), and the Rewind-to-Separation quicksaves under
    /// <c>Parsek/RewindPoints/</c> (`rp-quicksaves`). The pure halves of all three are
    /// covered headlessly by xUnit; these cells prove the same contracts on the real
    /// save folder KSP resolves at runtime.
    /// </summary>
    public class StorageRuntimeTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Storage",
            Description = "Recording-id path validation on the live save: real ids accepted and resolve inside the save, traversal and invalid characters rejected")]
        public void RecordingIdValidationOnLiveSave()
        {
            string saveDir = LiveSaveDirectory();
            if (saveDir == null)
            {
                InGameAssert.Skip("no loaded save folder");
                return;
            }

            // Rejected: traversal sequences, separators, and every invalid file-name char
            // the running platform reports (built into an otherwise valid id).
            var rejected = new List<string>
            {
                null, "", "..", "../escape", "..\\escape", "a/b", "a\\b", "ok..ok",
            };
            char[] invalid = Path.GetInvalidFileNameChars();
            int invalidCharIds = 0;
            for (int i = 0; i < invalid.Length; i++)
            {
                if (invalid[i] == '/' || invalid[i] == '\\') continue;
                rejected.Add("rec" + invalid[i] + "id");
                invalidCharIds++;
            }
            int rejectedOk = 0;
            for (int i = 0; i < rejected.Count; i++)
            {
                bool ok = RecordingPaths.ValidateRecordingId(rejected[i], RecordingIdValidationLogContext.Test);
                InGameAssert.IsFalse(ok, string.Format(IC,
                    "ValidateRecordingId accepted a hostile id (index {0}, length {1})",
                    i, rejected[i] != null ? rejected[i].Length : -1));
                rejectedOk++;
            }

            // Accepted: every id the live save carries, and each resolves to a sidecar
            // path that stays inside this save's Parsek/Recordings folder and exists.
            string recordingsDir = Path.GetFullPath(Path.Combine(saveDir, "Parsek", "Recordings"));
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            int acceptedRecordings = 0, resolvedOnDisk = 0;
            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                InGameAssert.IsTrue(
                    RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test),
                    "a real recording id from the live save was rejected: " + rec.RecordingId);
                acceptedRecordings++;
                string prec = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                InGameAssert.IsNotNull(prec, "trajectory path did not resolve for " + rec.RecordingId);
                InGameAssert.IsTrue(IsInside(prec, recordingsDir),
                    "trajectory path escaped the save's Recordings folder: " + prec);
                if (File.Exists(prec)) resolvedOnDisk++;
            }

            int acceptedRewindPoints = 0;
            List<RewindPoint> rps = LiveRewindPoints();
            string rpDir = Path.GetFullPath(Path.Combine(saveDir, "Parsek", "RewindPoints"));
            for (int i = 0; i < rps.Count; i++)
            {
                string id = rps[i] != null ? rps[i].RewindPointId : null;
                if (string.IsNullOrEmpty(id)) continue;
                InGameAssert.IsTrue(RecordingPaths.ValidateRecordingId(id, RecordingIdValidationLogContext.Test),
                    "a live rewind-point id was rejected: " + id);
                string rel = RecordingPaths.BuildRewindPointRelativePath(id);
                string abs = RecordingPaths.ResolveSaveScopedPath(rel);
                InGameAssert.IsTrue(abs != null && IsInside(abs, rpDir),
                    "rewind-point path escaped the save's RewindPoints folder: " + (abs ?? "<null>"));
                acceptedRewindPoints++;
            }

            ParsekLog.Info("StorageTest", string.Format(IC,
                "path validation on live save: rejected={0} invalidCharIds={1} acceptedRecordings={2} " +
                "resolvedOnDisk={3} acceptedRewindPoints={4}",
                rejectedOk, invalidCharIds, acceptedRecordings, resolvedOnDisk, acceptedRewindPoints));

            InGameAssert.IsTrue(acceptedRecordings > 0 || acceptedRewindPoints > 0,
                "the live save carries no recording or rewind-point id; the accept half would be vacuous");
        }

        [InGameTest(Category = "Storage",
            Description = "Readable .txt sidecar mirrors: the product reconcile removes them with the setting off and rewrites a faithful copy with it on")]
        public void ReadableSidecarMirrorsRoundTrip()
        {
            ParsekSettings settings = ParsekSettings.Current;
            if (settings == null || LiveSaveDirectory() == null)
            {
                InGameAssert.Skip("no live settings or save folder");
                return;
            }

            // Candidates: effective recordings whose authoritative .prec exists on disk.
            var candidates = new List<Recording>();
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec == null || !RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test))
                    continue;
                string prec = RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                if (prec != null && File.Exists(prec))
                    candidates.Add(rec);
            }
            if (candidates.Count == 0)
            {
                InGameAssert.Skip("no committed recording with a trajectory sidecar on disk");
                return;
            }

            bool prior = settings.writeReadableSidecarMirrors;
            int deletedWhenOff = 0, writtenWhenOn = 0, roundTripped = 0, vesselMirrors = 0;
            try
            {
                // OFF: the reconcile pass must remove every trajectory mirror.
                settings.writeReadableSidecarMirrors = false;
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
                for (int i = 0; i < candidates.Count; i++)
                {
                    string mirror = MirrorPath(candidates[i].RecordingId);
                    InGameAssert.IsFalse(File.Exists(mirror),
                        "readable mirror survived a reconcile with the setting off: " + mirror);
                    deletedWhenOff++;
                }

                // ON: the same pass writes each mirror back from the in-memory recording.
                settings.writeReadableSidecarMirrors = true;
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
                for (int i = 0; i < candidates.Count; i++)
                {
                    Recording rec = candidates[i];
                    string mirror = MirrorPath(rec.RecordingId);
                    InGameAssert.IsTrue(File.Exists(mirror),
                        "readable mirror not written with the setting on: " + mirror);
                    writtenWhenOn++;

                    ConfigNode node = ConfigNode.Load(mirror);
                    InGameAssert.IsNotNull(node, "readable mirror does not parse as a ConfigNode: " + mirror);
                    InGameAssert.AreEqual(rec.RecordingId, node.GetValue("recordingId"),
                        "mirror recordingId differs from its recording");
                    InGameAssert.AreEqual(
                        RecordingStore.CurrentRecordingSchemaGeneration.ToString(IC),
                        node.GetValue("recordingSchemaGeneration"),
                        "mirror schema generation differs from the current contract");
                    InGameAssert.AreEqual(rec.SidecarEpoch.ToString(IC), node.GetValue("sidecarEpoch"),
                        "mirror sidecarEpoch differs from the recording's");

                    // The mirror is a faithful readable image: it decodes back to the same
                    // trajectory shape as the live recording.
                    var decoded = new Recording { RecordingId = rec.RecordingId };
                    RecordingStore.DeserializeTrajectoryFrom(node, decoded);
                    InGameAssert.AreEqual(rec.Points.Count, decoded.Points.Count,
                        "mirror point count differs for " + rec.RecordingId);
                    InGameAssert.AreEqual(rec.PartEvents.Count, decoded.PartEvents.Count,
                        "mirror part-event count differs for " + rec.RecordingId);
                    InGameAssert.AreEqual(rec.TrackSections.Count, decoded.TrackSections.Count,
                        "mirror track-section count differs for " + rec.RecordingId);
                    roundTripped++;

                    string vesselMirror = RecordingPaths.ResolveSaveScopedPath(
                        RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(rec.RecordingId));
                    if (vesselMirror != null && File.Exists(vesselMirror)) vesselMirrors++;
                }
            }
            finally
            {
                settings.writeReadableSidecarMirrors = prior;
                RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings();
            }

            ParsekLog.Info("StorageTest", string.Format(IC,
                "readable mirror round trip: recordings={0} deletedWhenOff={1} writtenWhenOn={2} " +
                "roundTripped={3} vesselMirrors={4} settingRestored={5}",
                candidates.Count, deletedWhenOff, writtenWhenOn, roundTripped, vesselMirrors, prior));
        }

        [InGameTest(Category = "Storage",
            Description = "Every live rewind point has its quicksave at Parsek/RewindPoints/<rpId>.sfs, loadable as a KSP save, with no temp file left in the save root")]
        public void RewindPointQuicksavesOnDisk()
        {
            string saveDir = LiveSaveDirectory();
            List<RewindPoint> rps = LiveRewindPoints();
            if (saveDir == null || rps.Count == 0)
            {
                InGameAssert.Skip("no live rewind point; this test needs a run that authored one");
                return;
            }

            int checkedCount = 0, totalVessels = 0;
            long totalBytes = 0;
            for (int i = 0; i < rps.Count; i++)
            {
                RewindPoint rp = rps[i];
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) continue;

                string path = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildRewindPointRelativePath(rp.RewindPointId));
                InGameAssert.IsNotNull(path, "rewind-point path did not resolve for " + rp.RewindPointId);
                InGameAssert.IsTrue(File.Exists(path), "rewind-point quicksave missing: " + path);
                long bytes = new FileInfo(path).Length;
                InGameAssert.IsTrue(bytes > 0, "rewind-point quicksave is empty: " + path);

                ConfigNode root = ConfigNode.Load(path);
                InGameAssert.IsNotNull(root, "rewind-point quicksave does not parse: " + path);
                ConfigNode game = root.GetNode("GAME");
                InGameAssert.IsNotNull(game, "rewind-point quicksave has no GAME node: " + path);
                ConfigNode flight = game.GetNode("FLIGHTSTATE");
                InGameAssert.IsNotNull(flight, "rewind-point quicksave has no FLIGHTSTATE: " + path);
                int vessels = flight.GetNodes("VESSEL").Length;
                InGameAssert.IsTrue(vessels > 0, "rewind-point quicksave carries no vessel: " + path);

                double savedUt;
                InGameAssert.IsTrue(
                    double.TryParse(flight.GetValue("UT"), NumberStyles.Float, IC, out savedUt),
                    "rewind-point quicksave FLIGHTSTATE has no UT: " + path);
                InGameAssert.IsTrue(Math.Abs(savedUt - rp.UT) <= 5.0, string.Format(IC,
                    "rewind-point quicksave UT {0:F2} is not the rewind point's UT {1:F2}", savedUt, rp.UT));

                // The stock save lands in the save root first and is moved into
                // RewindPoints; no temp file may be left behind.
                string temp = Path.Combine(saveDir, "Parsek_TempRP_" + rp.RewindPointId + ".sfs");
                InGameAssert.IsFalse(File.Exists(temp), "rewind-point temp save left in the save root: " + temp);

                checkedCount++;
                totalVessels += vessels;
                totalBytes += bytes;
            }

            ParsekLog.Info("StorageTest", string.Format(IC,
                "rewind point quicksaves: checked={0} vessels={1} bytes={2}",
                checkedCount, totalVessels, totalBytes));
            InGameAssert.IsTrue(checkedCount > 0, "no rewind point carried an id");
        }

        private static string MirrorPath(string recordingId)
        {
            return RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(recordingId));
        }

        private static List<RewindPoint> LiveRewindPoints()
        {
            ParsekScenario scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
                return new List<RewindPoint>();
            return new List<RewindPoint>(scenario.RewindPoints);
        }

        private static string LiveSaveDirectory()
        {
            string root = KSPUtil.ApplicationRootPath;
            string folder = HighLogic.SaveFolder;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(folder))
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", folder));
        }

        private static bool IsInside(string path, string dir)
        {
            string full = Path.GetFullPath(path);
            string prefix = dir.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? dir
                : dir + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
