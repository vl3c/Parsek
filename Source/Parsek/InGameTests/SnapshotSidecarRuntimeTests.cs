using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek.InGameTests
{
    /// <summary>
    /// D16 snapshot-sidecar cells driven against the LIVE save: every `_vessel.craft` /
    /// `_ghost.craft` sidecar is the DeflateV1 container and decodes back to the snapshot
    /// the product loaded (`deflate-snapshots`), and a recording whose ghost snapshot
    /// aliases its vessel snapshot is written with NO `_ghost.craft` and re-loads with
    /// the ghost restored from the vessel (`alias-mode`).
    ///
    /// <para>Both cells drive the PRODUCT writer and reader
    /// (<c>RecordingSidecarStore.SaveRecordingFilesToPathsForTesting</c> /
    /// <c>LoadRecordingFilesFromPathsForTesting</c>, which are the path-explicit entry
    /// points of the same internal save and load bodies <c>SaveRecordingFiles</c> and
    /// <c>LoadRecordingFiles</c> run). The scratch copy lives under a fresh recording id
    /// in a scratch folder of the live save, so no committed recording's files are
    /// rewritten; the scratch folder and the scratch id's regenerable <c>.pann</c> are
    /// removed before the cell returns.</para>
    /// </summary>
    public class SnapshotSidecarRuntimeTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The DeflateV1 container magic (SnapshotSidecarCodec.Magic).</summary>
        private const string DeflateMagic = "PSN0";

        /// <summary>The DeflateV1 codec byte (SnapshotSidecarCodec.CodecDeflate).</summary>
        private const byte DeflateCodecByte = 1;

        [InGameTest(Category = "SnapshotSidecars",
            Description = "Every committed snapshot sidecar is the DeflateV1 container and decodes to the snapshot the product loaded; a live vessel snapshot round-trips through the product writer and reader")]
        public void SnapshotSidecarsAreDeflateV1AndRoundTrip()
        {
            string saveDir = LiveSaveDirectory();
            if (saveDir == null)
            {
                InGameAssert.Skip("no loaded save folder");
                return;
            }

            // Half 1: the sidecars the product already wrote for the save's committed
            // recordings. Each present file must probe as DeflateV1 and decode. Where the
            // product's own load kept the snapshot in memory the decode is compared with
            // it; a difference is COUNTED rather than failed, because several product
            // sites legitimately edit an in-memory snapshot after load (spawn offset,
            // crew reverse-mapping) and only the next save brings the file back in step.
            // At least one match is required, so a decode that garbles every file reds.
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            int files = 0, vesselFiles = 0, ghostFiles = 0, matchedInMemory = 0, differedInMemory = 0;
            long compressedBytes = 0, uncompressedBytes = 0;
            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec == null || !RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test))
                    continue;

                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));

                if (vesselPath != null && File.Exists(vesselPath))
                {
                    SnapshotSidecarProbe probe = AssertDeflateV1(vesselPath, out ConfigNode decoded);
                    files++; vesselFiles++;
                    compressedBytes += probe.CompressedLength;
                    uncompressedBytes += probe.UncompressedLength;
                    if (rec.VesselSnapshot != null)
                    {
                        if (RecordingStore.ConfigNodesEquivalent(rec.VesselSnapshot, decoded)) matchedInMemory++;
                        else differedInMemory++;
                    }
                }

                if (ghostPath != null && File.Exists(ghostPath))
                {
                    SnapshotSidecarProbe probe = AssertDeflateV1(ghostPath, out ConfigNode decoded);
                    files++; ghostFiles++;
                    compressedBytes += probe.CompressedLength;
                    uncompressedBytes += probe.UncompressedLength;
                    if (rec.GhostSnapshotMode == GhostSnapshotMode.Separate && rec.GhostVisualSnapshot != null)
                    {
                        if (RecordingStore.ConfigNodesEquivalent(rec.GhostVisualSnapshot, decoded)) matchedInMemory++;
                        else differedInMemory++;
                    }
                }
            }

            // Half 2: a snapshot captured from the live active vessel NOW (the product's
            // own capture, VesselSpawner.TryBackupSnapshot), written through the product
            // sidecar writer and read back through the product reader.
            int liveRoundTrips = 0;
            Vessel active = FlightGlobals.ActiveVessel;
            ConfigNode live = active != null ? VesselSpawner.TryBackupSnapshot(active) : null;
            if (live != null)
            {
                string scratchDir = CreateScratchDir(saveDir);
                try
                {
                    string path = Path.Combine(scratchDir, "live_vessel.craft");
                    RecordingStore.WriteSnapshotSidecarForTesting(path, live);
                    SnapshotSidecarProbe probe = AssertDeflateV1(path, out ConfigNode decoded);
                    InGameAssert.IsTrue(RecordingStore.ConfigNodesEquivalent(live, decoded),
                        "live vessel snapshot did not survive the DeflateV1 write/read round trip");
                    InGameAssert.IsTrue(probe.CompressedLength < probe.UncompressedLength, string.Format(IC,
                        "DeflateV1 did not compress the live snapshot ({0} -> {1} bytes)",
                        probe.UncompressedLength, probe.CompressedLength));
                    liveRoundTrips++;
                }
                finally
                {
                    DeleteScratchDir(scratchDir);
                }
            }

            ParsekLog.Info("SnapshotSidecarTest", string.Format(IC,
                "deflate snapshot sidecars: files={0} vessel={1} ghost={2} matchedInMemory={3} " +
                "differedInMemory={4} uncompressedBytes={5} compressedBytes={6} liveRoundTrips={7}",
                files, vesselFiles, ghostFiles, matchedInMemory, differedInMemory,
                uncompressedBytes, compressedBytes, liveRoundTrips));

            InGameAssert.IsTrue(files > 0,
                "the live save carries no snapshot sidecar; the on-disk half would be vacuous");
            InGameAssert.IsTrue(matchedInMemory > 0,
                "no decoded sidecar was compared against a snapshot the product loaded");
        }

        [InGameTest(Category = "SnapshotSidecars",
            Description = "A committed AliasVessel recording has no _ghost.craft on disk; the product save of a copy writes none and the product load restores the ghost from the vessel snapshot")]
        public void AliasVesselRecordingsWriteNoGhostSidecar()
        {
            string saveDir = LiveSaveDirectory();
            if (saveDir == null)
            {
                InGameAssert.Skip("no loaded save folder");
                return;
            }

            var aliases = new List<Recording>();
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec != null
                    && rec.GhostSnapshotMode == GhostSnapshotMode.AliasVessel
                    && RecordingPaths.ValidateRecordingId(rec.RecordingId, RecordingIdValidationLogContext.Test))
                    aliases.Add(rec);
            }
            if (aliases.Count == 0)
            {
                InGameAssert.Skip("no committed recording carries ghostSnapshotMode=AliasVessel; this test needs a fixture that has one");
                return;
            }

            int onDiskChecked = 0, rewritten = 0, reloaded = 0;
            for (int i = 0; i < aliases.Count; i++)
            {
                Recording rec = aliases[i];

                // The committed recording's own files: the vessel sidecar is present, the
                // ghost sidecar is not, because the product never wrote one.
                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                InGameAssert.IsTrue(vesselPath != null && File.Exists(vesselPath),
                    "AliasVessel recording has no _vessel.craft: " + rec.RecordingId);
                InGameAssert.IsTrue(ghostPath != null && !File.Exists(ghostPath),
                    "AliasVessel recording has a _ghost.craft on disk: " + rec.RecordingId);
                onDiskChecked++;

                // A copy under a scratch id, run through the product save and load. The
                // copy's snapshots are the committed recording's own; a dropped in-memory
                // vessel snapshot is re-hydrated from the committed sidecar first.
                Recording copy = Recording.DeepClone(rec);
                if (copy.VesselSnapshot == null)
                    RecordingSidecarStore.TryHydrateVesselSnapshotFromPath(copy, vesselPath);
                InGameAssert.IsNotNull(copy.VesselSnapshot,
                    "AliasVessel recording's vessel snapshot could not be loaded: " + rec.RecordingId);
                InGameAssert.AreEqual(GhostSnapshotMode.AliasVessel, RecordingStore.DetermineGhostSnapshotMode(copy),
                    "the product alias decision does not answer AliasVessel for " + rec.RecordingId);

                string scratchId = "ingamealias" + Guid.NewGuid().ToString("N");
                copy.RecordingId = scratchId;
                copy.SidecarEpoch = rec.SidecarEpoch;
                string scratchDir = CreateScratchDir(saveDir);
                try
                {
                    string sPrec = Path.Combine(scratchDir, scratchId + ".prec");
                    string sVessel = Path.Combine(scratchDir, scratchId + "_vessel.craft");
                    string sGhost = Path.Combine(scratchDir, scratchId + "_ghost.craft");

                    InGameAssert.IsTrue(RecordingSidecarStore.SaveRecordingFilesToPathsForTesting(
                            copy, sPrec, sVessel, sGhost, incrementEpoch: false),
                        "product save of the AliasVessel copy failed for " + rec.RecordingId);
                    InGameAssert.AreEqual(GhostSnapshotMode.AliasVessel, copy.GhostSnapshotMode,
                        "product save did not stamp AliasVessel on the copy of " + rec.RecordingId);
                    InGameAssert.IsTrue(File.Exists(sVessel),
                        "product save wrote no _vessel.craft for the AliasVessel copy of " + rec.RecordingId);
                    InGameAssert.IsFalse(File.Exists(sGhost),
                        "product save wrote a _ghost.craft for the AliasVessel copy of " + rec.RecordingId);
                    AssertDeflateV1(sVessel, out ConfigNode _);
                    rewritten++;

                    // The load side reads what the .sfs metadata would carry: id, schema,
                    // epoch and the stamped mode.
                    var reread = new Recording
                    {
                        RecordingId = scratchId,
                        RecordingFormatVersion = copy.RecordingFormatVersion,
                        RecordingSchemaGeneration = copy.RecordingSchemaGeneration,
                        SidecarEpoch = copy.SidecarEpoch,
                        GhostSnapshotMode = copy.GhostSnapshotMode,
                    };
                    InGameAssert.IsTrue(RecordingSidecarStore.LoadRecordingFilesFromPathsForTesting(
                            reread, sPrec, sVessel, sGhost),
                        "product load of the AliasVessel copy failed for " + rec.RecordingId
                        + " reason=" + (reread.SidecarLoadFailureReason ?? "<none>"));
                    InGameAssert.AreEqual(GhostSnapshotMode.AliasVessel, reread.GhostSnapshotMode,
                        "reloaded copy lost AliasVessel for " + rec.RecordingId);
                    InGameAssert.IsNotNull(reread.VesselSnapshot,
                        "reloaded copy has no vessel snapshot for " + rec.RecordingId);
                    InGameAssert.IsNotNull(reread.GhostVisualSnapshot,
                        "reloaded copy has no ghost snapshot for " + rec.RecordingId);
                    InGameAssert.IsTrue(RecordingStore.ConfigNodesEquivalent(copy.VesselSnapshot, reread.VesselSnapshot),
                        "reloaded vessel snapshot differs from the saved one for " + rec.RecordingId);
                    InGameAssert.IsTrue(RecordingStore.ConfigNodesEquivalent(reread.VesselSnapshot, reread.GhostVisualSnapshot),
                        "reloaded ghost snapshot is not the vessel snapshot for " + rec.RecordingId);
                    reloaded++;
                }
                finally
                {
                    DeleteScratchDir(scratchDir);
                    DeleteScratchAnnotation(scratchId);
                }
            }

            ParsekLog.Info("SnapshotSidecarTest", string.Format(IC,
                "alias-vessel sidecars: recordings={0} onDiskChecked={1} rewritten={2} reloaded={3}",
                aliases.Count, onDiskChecked, rewritten, reloaded));
        }

        /// <summary>Probe + decode one sidecar and assert the DeflateV1 container.</summary>
        private static SnapshotSidecarProbe AssertDeflateV1(string path, out ConfigNode decoded)
        {
            byte[] head = new byte[DeflateMagic.Length];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read = stream.Read(head, 0, head.Length);
                InGameAssert.AreEqual(head.Length, read, "snapshot sidecar shorter than its magic: " + path);
            }
            InGameAssert.AreEqual(DeflateMagic, System.Text.Encoding.ASCII.GetString(head),
                "snapshot sidecar does not start with the DeflateV1 magic: " + path);

            InGameAssert.IsTrue(RecordingStore.TryLoadSnapshotSidecar(path, out decoded, out SnapshotSidecarProbe probe),
                "snapshot sidecar failed to decode: " + path + " " + SnapshotSidecarCodec.DescribeProbe(probe));
            InGameAssert.IsTrue(probe.Supported, "snapshot sidecar is not supported: " + path);
            InGameAssert.AreEqual(SnapshotSidecarEncoding.DeflateV1, probe.Encoding,
                "snapshot sidecar encoding is not DeflateV1: " + path);
            InGameAssert.AreEqual(DeflateCodecByte, probe.Codec, "snapshot sidecar codec byte is not Deflate: " + path);
            InGameAssert.AreEqual(SnapshotSidecarCodec.CurrentVersion, probe.FormatVersion,
                "snapshot sidecar format version differs from the current codec: " + path);
            InGameAssert.AreEqual(RecordingStore.CurrentRecordingSchemaGeneration, probe.SchemaGeneration,
                "snapshot sidecar schema generation differs from the current contract: " + path);
            InGameAssert.IsTrue(probe.CompressedLength > 0 && probe.UncompressedLength > 0,
                "snapshot sidecar carries an empty payload: " + path);
            InGameAssert.IsNotNull(decoded, "snapshot sidecar decoded to no node: " + path);
            return probe;
        }

        private static string CreateScratchDir(string saveDir)
        {
            string dir = Path.Combine(saveDir, "Parsek", "InGameScratch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteScratchDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SnapshotSidecarTest", "scratch folder cleanup failed: " + dir + " " + ex.Message);
            }
        }

        /// <summary>The product save and load resolve the regenerable smoothing cache from
        /// the recording id alone, so the scratch id's <c>.pann</c> lands in the live
        /// Recordings folder and is removed here.</summary>
        private static void DeleteScratchAnnotation(string scratchId)
        {
            string pann = RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildAnnotationsRelativePath(scratchId));
            try
            {
                if (!string.IsNullOrEmpty(pann) && File.Exists(pann))
                    File.Delete(pann);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SnapshotSidecarTest", "scratch annotation cleanup failed: " + pann + " " + ex.Message);
            }
        }

        private static string LiveSaveDirectory()
        {
            string root = KSPUtil.ApplicationRootPath;
            string folder = HighLogic.SaveFolder;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(folder))
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", folder));
        }
    }
}
