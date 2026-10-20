using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// design-load-restores-the-save.md 4.4: content hashes, retained versions and the
    /// Cold-load version resolution. Every cell drives real file I/O in a temp save root.
    /// </summary>
    [Collection("Sequential")]
    public class SidecarVersionRetentionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;
        private readonly string recordingsDir;
        private readonly string versionsDir;

        public SidecarVersionRetentionTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            SidecarVersionResolution.ResetForTesting();
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;

            saveRoot = Path.Combine(Path.GetTempPath(), "parsek-sidecar-versions-" + Guid.NewGuid().ToString("N"));
            recordingsDir = Path.Combine(saveRoot, "Parsek", "Recordings");
            versionsDir = Path.Combine(recordingsDir, SidecarVersions.VersionsDirName);
            Directory.CreateDirectory(recordingsDir);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            SidecarVersionResolution.ResetForTesting();
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            Parsek.Rendering.SmoothingPipeline.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { Directory.Delete(saveRoot, true); } catch { }
        }

        // ---------- helpers ----------

        private static Recording MakeRecording(string id, int pointCount, double startUt = 100)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Probe " + id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
            };
            for (int i = 0; i < pointCount; i++)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = startUt + i,
                    latitude = 1.0 + i * 0.001,
                    longitude = 2.0,
                    altitude = 100 + i,
                    bodyName = "Kerbin",
                    rotation = new UnityEngine.Quaternion(0, 0, 0, 1),
                });
            }
            return rec;
        }

        private static ConfigNode MakeVesselSnapshot(string name)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", name);
            node.AddValue("pid", "0f0e0d0c0b0a09080706050403020100");
            return node;
        }

        private string PrecPath(string id) => Path.Combine(recordingsDir, id + ".prec");
        private string VesselPath(string id) => Path.Combine(recordingsDir, id + "_vessel.craft");
        private string GhostPath(string id) => Path.Combine(recordingsDir, id + "_ghost.craft");
        private string PannPath(string id) => Path.Combine(recordingsDir, id + ".pann");

        private void Save(Recording rec, bool incrementEpoch = true)
        {
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, PrecPath(rec.RecordingId), VesselPath(rec.RecordingId), GhostPath(rec.RecordingId),
                incrementEpoch));
        }

        private string[] VersionFiles()
        {
            return Directory.Exists(versionsDir) ? Directory.GetFiles(versionsDir) : new string[0];
        }

        /// <summary>A RECORDING node round trip: what a save would name for this recording.</summary>
        private static Recording SavedCopy(Recording rec)
        {
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);
            return loaded;
        }

        private static Dictionary<string, int> Owners(string id, int count)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal) { { id, count } };
        }

        // ---------- hash ----------

        [Fact]
        public void Compute_IsFirst64BitsOfSha1_LowerHex()
        {
            // SHA-1("abc") = a9993e364706816aba3e25717850c26c9cd0d89d
            Assert.Equal("a9993e364706816a",
                SidecarContentHash.Compute(System.Text.Encoding.ASCII.GetBytes("abc")));
            Assert.True(SidecarContentHash.IsWellFormed("a9993e364706816a"));
            Assert.False(SidecarContentHash.IsWellFormed("A9993E364706816A"));
            Assert.False(SidecarContentHash.IsWellFormed("a9993e36470681"));
        }

        [Fact]
        public void VersionFileNames_RoundTrip_AllKinds()
        {
            foreach (SidecarKind kind in new[] { SidecarKind.Prec, SidecarKind.Vessel, SidecarKind.Ghost })
            {
                string name = SidecarVersions.VersionFileName("rec.with.dots", "0123456789abcdef", kind);
                Assert.True(SidecarVersions.TryParseVersionFileName(name, out string id, out string hash, out SidecarKind k));
                Assert.Equal("rec.with.dots", id);
                Assert.Equal("0123456789abcdef", hash);
                Assert.Equal(kind, k);

                string incoming = SidecarVersions.IncomingFileName("rec1", kind);
                Assert.False(SidecarVersions.TryParseVersionFileName(incoming, out _, out _, out _));
                Assert.True(SidecarVersions.TryParseIncomingFileName(incoming, out string inId, out SidecarKind inKind));
                Assert.Equal("rec1", inId);
                Assert.Equal(kind, inKind);
            }
            Assert.False(SidecarVersions.TryParseVersionFileName("rec1.prec", out _, out _, out _));
            Assert.False(SidecarVersions.TryParseVersionFileName("rec1.nothex0000000000.prec", out _, out _, out _));
        }

        // ---------- codec ----------

        [Fact]
        public void Codec_HashesRoundTrip_Sparse_MalformedDropped()
        {
            var rec = new Recording
            {
                RecordingId = "r1",
                PrecHash = "0123456789abcdef",
                VesselSnapshotHash = "fedcba9876543210",
            };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            Assert.Equal("0123456789abcdef", node.GetValue("precHash"));
            Assert.Equal("fedcba9876543210", node.GetValue("vesselHash"));
            Assert.Null(node.GetValue("ghostHash"));

            var loaded = new Recording { GhostSnapshotHash = "1111111111111111" };
            RecordingTree.LoadRecordingFrom(node, loaded);
            Assert.Equal("0123456789abcdef", loaded.PrecHash);
            Assert.Equal("fedcba9876543210", loaded.VesselSnapshotHash);
            // A missing key means "no hash", never "keep what the object held".
            Assert.Null(loaded.GhostSnapshotHash);

            node.SetValue("precHash", "NOT-A-HASH");
            var malformed = new Recording();
            RecordingTree.LoadRecordingFrom(node, malformed);
            Assert.Null(malformed.PrecHash);
            Assert.Contains(logLines, l => l.Contains("[Codec]") && l.Contains("malformed precHash"));
        }

        [Fact]
        public void DeepClone_CopiesHashes()
        {
            var rec = new Recording
            {
                RecordingId = "r1",
                PrecHash = "0123456789abcdef",
                VesselSnapshotHash = "fedcba9876543210",
                GhostSnapshotHash = "1111111111111111",
            };
            Recording clone = Recording.DeepClone(rec);
            Assert.Equal(rec.PrecHash, clone.PrecHash);
            Assert.Equal(rec.VesselSnapshotHash, clone.VesselSnapshotHash);
            Assert.Equal(rec.GhostSnapshotHash, clone.GhostSnapshotHash);
        }

        // ---------- write side + retention ----------

        [Fact]
        public void Write_SetsHashesFromBytesWritten()
        {
            var rec = MakeRecording("w1", 5);
            rec.VesselSnapshot = MakeVesselSnapshot("v");
            Save(rec);

            Assert.Equal(SidecarContentHash.ComputeFile(PrecPath("w1")), rec.PrecHash);
            Assert.Equal(SidecarContentHash.ComputeFile(VesselPath("w1")), rec.VesselSnapshotHash);
            Assert.Null(rec.GhostSnapshotHash);
            Assert.Empty(VersionFiles());
        }

        [Fact]
        public void Rewrite_FilesReplacedHeadUnderItsOwnHash_EvenWhenMemoryHashIsWrong()
        {
            var rec = MakeRecording("w2", 5);
            Save(rec);
            string oldHash = SidecarContentHash.ComputeFile(PrecPath("w2"));
            byte[] oldBytes = File.ReadAllBytes(PrecPath("w2"));

            // Memory's idea of the head is never trusted for filing.
            rec.PrecHash = "0000000000000000";
            rec.Points.Add(new TrajectoryPoint { ut = 500, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);

            string version = Path.Combine(versionsDir, SidecarVersions.VersionFileName("w2", oldHash, SidecarKind.Prec));
            Assert.True(File.Exists(version));
            Assert.Equal(oldBytes, File.ReadAllBytes(version));
            Assert.Equal(SidecarContentHash.ComputeFile(PrecPath("w2")), rec.PrecHash);
            Assert.NotEqual(oldHash, rec.PrecHash);
            Assert.Single(VersionFiles());
            Assert.Contains(logLines, l => l.Contains("Sidecar retained: rec=w2 kind=prec")
                && l.Contains("hash=" + oldHash) && l.Contains("reason=rewrite") && l.Contains("outcome=filed"));
        }

        [Fact]
        public void ByteIdenticalRewrite_RetainsNothing()
        {
            var rec = MakeRecording("w3", 5);
            Save(rec, incrementEpoch: false);
            Save(rec, incrementEpoch: false);

            Assert.Empty(VersionFiles());
            Assert.Contains(logLines, l => l.Contains("Sidecar retained: rec=w3 kind=prec")
                && l.Contains("outcome=same-as-head"));
        }

        [Fact]
        public void Rewrite_ExistingIdenticalTarget_DropsIncoming()
        {
            var rec = MakeRecording("w4", 5);
            Save(rec);
            string oldHash = SidecarContentHash.ComputeFile(PrecPath("w4"));
            Directory.CreateDirectory(versionsDir);
            string target = Path.Combine(versionsDir, SidecarVersions.VersionFileName("w4", oldHash, SidecarKind.Prec));
            File.Copy(PrecPath("w4"), target);

            Save(rec);

            Assert.Single(VersionFiles());
            Assert.True(File.Exists(target));
            Assert.Contains(logLines, l => l.Contains("Sidecar retained: rec=w4") && l.Contains("outcome=identical-exists"));
        }

        [Fact]
        public void FilingFailure_LeavesIncomingForTheCollector()
        {
            var rec = MakeRecording("w5", 5);
            Save(rec);
            string oldHash = SidecarContentHash.ComputeFile(PrecPath("w5"));
            // A directory squatting on the target name makes the rename fail.
            Directory.CreateDirectory(Path.Combine(versionsDir, SidecarVersions.VersionFileName("w5", oldHash, SidecarKind.Prec)));

            rec.Points.Add(new TrajectoryPoint { ut = 500, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);

            string[] incoming = Directory.GetFiles(versionsDir, "*" + SidecarVersions.IncomingMarker + "*");
            Assert.Single(incoming);
            Assert.Equal(oldHash, SidecarContentHash.ComputeFile(incoming[0]));
            Assert.Contains(logLines, l => l.Contains("Sidecar retained: rec=w5") && l.Contains("outcome=left-incoming"));
        }

        [Fact]
        public void DeleteRecordingFiles_RetainsHashedKinds_DeletesTheRest()
        {
            var rec = MakeRecording("d1", 5);
            rec.VesselSnapshot = MakeVesselSnapshot("v");
            rec.RewindSaveFileName = "parsek_rw_abc123";
            Save(rec);
            File.WriteAllText(PannPath("d1"), "pann");
            File.WriteAllText(PrecPath("d1") + ".txt", "mirror");
            string savesDir = Path.Combine(saveRoot, "Parsek", "Saves");
            Directory.CreateDirectory(savesDir);
            string rewindSave = Path.Combine(savesDir, "parsek_rw_abc123.sfs");
            File.WriteAllText(rewindSave, "GAME\n{\n}\n");
            string precHash = rec.PrecHash;
            string vesselHash = rec.VesselSnapshotHash;

            Assert.True(RecordingStore.DeleteRecordingFiles(rec));

            Assert.False(File.Exists(PrecPath("d1")));
            Assert.False(File.Exists(VesselPath("d1")));
            Assert.False(File.Exists(PannPath("d1")));
            Assert.False(File.Exists(PrecPath("d1") + ".txt"));
            Assert.True(File.Exists(Path.Combine(versionsDir, SidecarVersions.VersionFileName("d1", precHash, SidecarKind.Prec))));
            Assert.True(File.Exists(Path.Combine(versionsDir, SidecarVersions.VersionFileName("d1", vesselHash, SidecarKind.Vessel))));
            // The rewind save is the collector's to delete.
            Assert.True(File.Exists(rewindSave));
            Assert.Contains(logLines, l => l.Contains("Sidecar retained: rec=d1 kind=prec") && l.Contains("reason=delete"));
        }

        [Fact]
        public void FailedBatch_RollsBackHead_AndFilesNothing()
        {
            var rec = MakeRecording("rb1", 5);
            Save(rec);
            byte[] original = File.ReadAllBytes(PrecPath("rb1"));

            string staged = PrecPath("rb1") + ".stage.test";
            File.WriteAllText(staged, "new content");
            var changes = new List<SidecarFileCommitBatch.StagedChange>
            {
                new SidecarFileCommitBatch.StagedChange
                {
                    FinalPath = PrecPath("rb1"), StagedPath = staged,
                    RetainKind = SidecarKind.Prec, RetainRecordingId = "rb1",
                },
                // Second change fails: its staged file does not exist.
                new SidecarFileCommitBatch.StagedChange
                {
                    FinalPath = VesselPath("rb1"), StagedPath = VesselPath("rb1") + ".stage.missing",
                },
            };

            Assert.ThrowsAny<Exception>(() => SidecarFileCommitBatch.Apply(changes, () => false));

            Assert.Equal(original, File.ReadAllBytes(PrecPath("rb1")));
            Assert.Empty(VersionFiles());
        }

        // ---------- read side ----------

        [Fact]
        public void ColdLoad_HeadMatches_ReadsHead()
        {
            var rec = MakeRecording("c1", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);

            SidecarVersionResolution.BeginForTesting(Owners("c1", 1));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Equal(5, saved.Points.Count);
            Assert.Equal(rec.PrecHash, saved.PrecHash);
            Assert.True(saved.SidecarVersionsResolved);
            Assert.Contains(logLines, l => l.Contains("Sidecar version resolved: rec=c1 kind=prec") && l.Contains("source=head"));
            Assert.Contains(logLines, l => l.Contains("Sidecar version resolution summary") && l.Contains("head=1"));
        }

        [Fact]
        public void ColdLoad_VersionNamed_SingleOwner_ReadsAndPromotes()
        {
            // S2: the save names the F5 bytes; two later writes moved the head on.
            var rec = MakeRecording("c2", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);
            string f5Hash = rec.PrecHash;
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);
            rec.Points.Add(new TrajectoryPoint { ut = 901, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);
            string newestHash = rec.PrecHash;

            SidecarVersionResolution.BeginForTesting(Owners("c2", 1));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Equal(5, saved.Points.Count);
            Assert.Equal(f5Hash, saved.PrecHash);
            // Promoted: the head is the F5 bytes again, the newest head is retained.
            Assert.Equal(f5Hash, SidecarContentHash.ComputeFile(PrecPath("c2")));
            Assert.False(File.Exists(Path.Combine(versionsDir, SidecarVersions.VersionFileName("c2", f5Hash, SidecarKind.Prec))));
            Assert.True(File.Exists(Path.Combine(versionsDir, SidecarVersions.VersionFileName("c2", newestHash, SidecarKind.Prec))));
            Assert.Contains(logLines, l => l.Contains("Sidecar version resolved: rec=c2 kind=prec")
                && l.Contains("source=version") && l.Contains("promoted=True"));
        }

        [Fact]
        public void ColdLoad_VersionNamed_TwoOwners_ReadsVersionWithoutPromote()
        {
            var rec = MakeRecording("c3", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);
            string f5Hash = rec.PrecHash;
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);
            string headHash = rec.PrecHash;

            SidecarVersionResolution.BeginForTesting(Owners("c3", 2));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Equal(5, saved.Points.Count);
            Assert.Equal(headHash, SidecarContentHash.ComputeFile(PrecPath("c3")));
            Assert.True(File.Exists(Path.Combine(versionsDir, SidecarVersions.VersionFileName("c3", f5Hash, SidecarKind.Prec))));
            Assert.Contains(logLines, l => l.Contains("Sidecar version read without promote: rec=c3 kind=prec")
                && l.Contains("reason=multiple-owners"));
        }

        [Fact]
        public void ColdLoad_PromoteFailure_ReadsFromVersionPath()
        {
            var rec = MakeRecording("c4", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);
            string f5Hash = rec.PrecHash;
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);
            // Replace the head file by a directory so the move into the head position fails.
            File.Delete(PrecPath("c4"));
            Directory.CreateDirectory(PrecPath("c4"));

            SidecarVersionResolution.BeginForTesting(Owners("c4", 1));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Equal(5, saved.Points.Count);
            Assert.True(File.Exists(Path.Combine(versionsDir, SidecarVersions.VersionFileName("c4", f5Hash, SidecarKind.Prec))));
            Assert.Contains(logLines, l => l.Contains("Sidecar version read without promote: rec=c4")
                && l.Contains("reason=promote-failed"));
        }

        /// <summary>
        /// Writes <paramref name="bytes"/> as the retained version of one kind and returns
        /// its resolution (the save names that hash, the head holds other bytes or nothing).
        /// </summary>
        private SidecarResolution WriteVersion(string id, SidecarKind kind, byte[] bytes, string headPath)
        {
            string hash = SidecarContentHash.Compute(bytes);
            Directory.CreateDirectory(versionsDir);
            File.WriteAllBytes(Path.Combine(versionsDir, SidecarVersions.VersionFileName(id, hash, kind)), bytes);
            SidecarResolution r = SidecarVersionResolution.Resolve(headPath, id, kind, hash);
            Assert.Equal(SidecarResolveSource.Version, r.Source);
            return r;
        }

        [Fact]
        public void TryPromote_LaterChangeFails_KeepsVersionBytesAtTheReadPath_AndTheOldHead()
        {
            byte[] a = System.Text.Encoding.ASCII.GetBytes("head bytes A");
            byte[] b = System.Text.Encoding.ASCII.GetBytes("version bytes B");
            byte[] d = System.Text.Encoding.ASCII.GetBytes("vessel version bytes D");
            File.WriteAllBytes(PrecPath("pf1"), a);
            SidecarResolution prec = WriteVersion("pf1", SidecarKind.Prec, b, PrecPath("pf1"));
            // The vessel head path is a directory, so the second change throws.
            Directory.CreateDirectory(VesselPath("pf1"));
            SidecarResolution vessel = WriteVersion("pf1", SidecarKind.Vessel, d, VesselPath("pf1"));

            Assert.False(SidecarVersionResolution.TryPromote(
                "pf1", new List<SidecarResolution> { prec, vessel }, out string failure));
            Assert.NotNull(failure);

            // Not promoted: the recording reads the version paths, which still hold B and D.
            Assert.Equal(prec.VersionPath, prec.ReadPath);
            Assert.Equal(b, File.ReadAllBytes(prec.ReadPath));
            Assert.Equal(d, File.ReadAllBytes(vessel.ReadPath));
            // The old head is still retained in the head position.
            Assert.Equal(a, File.ReadAllBytes(PrecPath("pf1")));
            Assert.True(Directory.Exists(VesselPath("pf1")));
        }

        [Fact]
        public void TryPromote_NoHead_LaterChangeFails_KeepsVersionBytes_AndCreatesNoHead()
        {
            byte[] b = System.Text.Encoding.ASCII.GetBytes("version bytes B");
            byte[] d = System.Text.Encoding.ASCII.GetBytes("vessel version bytes D");
            SidecarResolution prec = WriteVersion("pf2", SidecarKind.Prec, b, PrecPath("pf2"));
            Directory.CreateDirectory(VesselPath("pf2"));
            SidecarResolution vessel = WriteVersion("pf2", SidecarKind.Vessel, d, VesselPath("pf2"));

            Assert.False(SidecarVersionResolution.TryPromote(
                "pf2", new List<SidecarResolution> { prec, vessel }, out string _));

            Assert.Equal(b, File.ReadAllBytes(prec.ReadPath));
            Assert.Equal(d, File.ReadAllBytes(vessel.ReadPath));
            Assert.False(File.Exists(PrecPath("pf2")));
        }

        [Fact]
        public void TryPromote_Success_MovesEveryVersionToHead_AndRetainsTheOldHead()
        {
            byte[] a = System.Text.Encoding.ASCII.GetBytes("head bytes A");
            byte[] b = System.Text.Encoding.ASCII.GetBytes("version bytes B");
            byte[] d = System.Text.Encoding.ASCII.GetBytes("vessel version bytes D");
            File.WriteAllBytes(PrecPath("pf3"), a);
            SidecarResolution prec = WriteVersion("pf3", SidecarKind.Prec, b, PrecPath("pf3"));
            SidecarResolution vessel = WriteVersion("pf3", SidecarKind.Vessel, d, VesselPath("pf3"));

            Assert.True(SidecarVersionResolution.TryPromote(
                "pf3", new List<SidecarResolution> { prec, vessel }, out string _));

            Assert.Equal(b, File.ReadAllBytes(PrecPath("pf3")));
            Assert.Equal(d, File.ReadAllBytes(VesselPath("pf3")));
            Assert.False(File.Exists(prec.VersionPath));
            Assert.False(File.Exists(vessel.VersionPath));
            Assert.True(File.Exists(Path.Combine(versionsDir,
                SidecarVersions.VersionFileName("pf3", SidecarContentHash.Compute(a), SidecarKind.Prec))));
            // Nothing transient is left behind.
            Assert.DoesNotContain(Directory.GetFiles(recordingsDir), f => f.Contains(".stage."));
            Assert.DoesNotContain(VersionFiles(), f => f.Contains(SidecarVersions.IncomingMarker));
        }

        [Fact]
        public void ColdLoad_NamedHashMissingEverywhere_FailsAndKeepsNamedHash()
        {
            var rec = MakeRecording("c5", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);
            string named = rec.PrecHash;
            // The head moves on and the version is externally deleted.
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec);
            foreach (string f in VersionFiles()) File.Delete(f);

            SidecarVersionResolution.BeginForTesting(Owners("c5", 1));
            Assert.False(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.True(saved.SidecarLoadFailed);
            Assert.Equal("stale-sidecar-epoch", saved.SidecarLoadFailureReason);
            // Never laundered: the next save names the same bytes again.
            Assert.Equal(named, saved.PrecHash);
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, saved);
            Assert.Equal(named, node.GetValue("precHash"));
            Assert.Contains(logLines, l => l.Contains("Sidecar version unresolved: rec=c5 kind=prec")
                && l.Contains("hash=" + named));
        }

        [Fact]
        public void ColdLoad_NamedHashNoHeadNoVersion_IsTrajectoryMissing()
        {
            var saved = MakeRecording("c6", 0);
            saved.PrecHash = "0123456789abcdef";

            SidecarVersionResolution.BeginForTesting(Owners("c6", 1));
            Assert.False(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Equal("trajectory-missing", saved.SidecarLoadFailureReason);
            Assert.Equal("0123456789abcdef", saved.PrecHash);
        }

        [Fact]
        public void HashlessSave_UsesEpochRule_AndHashesTheHeadItRead()
        {
            var rec = MakeRecording("c7", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);
            saved.PrecHash = null;

            SidecarVersionResolution.BeginForTesting(Owners("c7", 1));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");
            Assert.Equal(SidecarContentHash.ComputeFile(PrecPath("c7")), saved.PrecHash);

            // Epoch rule still rejects a stale head when no hash is named.
            Recording stale = SavedCopy(rec);
            stale.PrecHash = null;
            stale.SidecarEpoch = rec.SidecarEpoch + 5;
            SidecarVersionResolution.BeginForTesting(Owners("c7", 1));
            Assert.False(RecordingStore.LoadRecordingFiles(stale));
            SidecarVersionResolution.End("test");
            Assert.Equal("stale-sidecar-epoch", stale.SidecarLoadFailureReason);
            Assert.Null(stale.PrecHash);
        }

        [Fact]
        public void NonColdLoad_IgnoresHashes_ReadsHeadWithEpochRule()
        {
            var rec = MakeRecording("n1", 5);
            Save(rec, incrementEpoch: true);
            Recording saved = SavedCopy(rec);
            // Same-epoch rewrite: the epoch rule accepts the head.
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec, incrementEpoch: false);

            Assert.False(SidecarVersionResolution.IsActive);
            Assert.True(RecordingStore.LoadRecordingFiles(saved));

            Assert.Equal(6, saved.Points.Count);
            Assert.False(saved.SidecarVersionsResolved);
            Assert.Equal(rec.PrecHash, saved.PrecHash);
            Assert.DoesNotContain(logLines, l => l.Contains("Sidecar version resolved"));
        }

        [Fact]
        public void ColdLoad_SameEpochRewrite_IsCaughtByTheHash()
        {
            // S7: an optimizer flush rewrote the head under the same epoch after the save.
            var rec = MakeRecording("s7", 5);
            Save(rec, incrementEpoch: true);
            Recording saved = SavedCopy(rec);
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec, incrementEpoch: false);
            Assert.Equal(saved.SidecarEpoch, rec.SidecarEpoch);

            SidecarVersionResolution.BeginForTesting(Owners("s7", 1));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Equal(5, saved.Points.Count);
        }

        [Fact]
        public void ColdLoad_VersionRead_RecomputesAnnotationCache()
        {
            var rec = MakeRecording("p1", 5);
            Save(rec);
            Recording saved = SavedCopy(rec);
            rec.Points.Add(new TrajectoryPoint { ut = 900, bodyName = "Kerbin", rotation = new UnityEngine.Quaternion(0, 0, 0, 1) });
            Save(rec, incrementEpoch: false);
            File.WriteAllText(PannPath("p1"), "stale annotation bytes");

            SidecarVersionResolution.BeginForTesting(Owners("p1", 2));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");

            Assert.Contains(logLines, l => l.Contains("annotation cache dropped: rec=p1 reason=version-read"));
            Assert.True(PannotationsSidecarBinary.TryProbe(PannPath("p1"), out PannotationsSidecarProbe probe));
            Assert.True(probe.Success);
        }

        [Fact]
        public void ColdLoad_SnapshotVersion_ResolvedAndLazyHydrationFollowsIt()
        {
            var rec = MakeRecording("v1", 5);
            rec.VesselSnapshot = MakeVesselSnapshot("old");
            Save(rec);
            Recording saved = SavedCopy(rec);
            string oldVesselHash = rec.VesselSnapshotHash;
            rec.VesselSnapshot = MakeVesselSnapshot("new");
            Save(rec);

            SidecarVersionResolution.BeginForTesting(Owners("v1", 2));
            Assert.True(RecordingStore.LoadRecordingFiles(saved));
            SidecarVersionResolution.End("test");
            Assert.Equal("old", saved.VesselSnapshot.GetValue("name"));
            Assert.Equal(oldVesselHash, saved.VesselSnapshotHash);

            // The transient snapshot is dropped; the lazy hydration reads the same version.
            saved.VesselSnapshot = null;
            Assert.True(RecordingStore.TryHydrateVesselSnapshotFromSidecar(saved));
            Assert.Equal("old", saved.VesselSnapshot.GetValue("name"));
        }

        [Fact]
        public void CountRecordingOwners_CountsEveryTreeAndTopLevelNode()
        {
            var scenario = new ConfigNode("SCENARIO");
            var committed = scenario.AddNode("RECORDING_TREE");
            committed.AddNode("RECORDING").AddValue("recordingId", "a");
            committed.AddNode("RECORDING").AddValue("recordingId", "b");
            var active = scenario.AddNode("RECORDING_TREE");
            active.AddValue("isActive", "True");
            active.AddNode("RECORDING").AddValue("recordingId", "a");
            scenario.AddNode("RECORDING").AddValue("recordingId", "c");
            // GAME_ACTION rows carry recordingId too and are not owners.
            scenario.AddNode("LEDGER").AddNode("GAME_ACTION").AddValue("recordingId", "b");

            Dictionary<string, int> owners = SidecarVersionResolution.CountRecordingOwners(scenario);
            Assert.Equal(2, owners["a"]);
            Assert.Equal(1, owners["b"]);
            Assert.Equal(1, owners["c"]);
        }

        [Fact]
        public void ReFlyTempSave_StripsHashes_WhenEpochAlignedToHead()
        {
            var recNode = new ConfigNode("RECORDING");
            recNode.AddValue("recordingId", "x");
            recNode.AddValue("precHash", "0123456789abcdef");
            recNode.AddValue("vesselHash", "0123456789abcdef");
            Assert.Equal(2, RewindInvoker.StripSidecarHashes(recNode));
            Assert.Null(recNode.GetValue("precHash"));
            Assert.Null(recNode.GetValue("vesselHash"));
            Assert.Equal("x", recNode.GetValue("recordingId"));
        }

        [Fact]
        public void ReFlyTempSave_EpochRefresh_StripsHashesOfRefreshedNodes()
        {
            var rec = MakeRecording("rf1", 5);
            Save(rec);
            var root = new ConfigNode("GAME");
            var scenario = root.AddNode("SCENARIO");
            var recNode = scenario.AddNode("RECORDING_TREE").AddNode("RECORDING");
            recNode.AddValue("recordingId", "rf1");
            recNode.AddValue("sidecarEpoch", "0");
            recNode.AddValue("precHash", "0123456789abcdef");
            string sfsPath = Path.Combine(saveRoot, "temp.sfs");

            RewindInvoker.RefreshRecordingSidecarEpochsForTempSave(root, sfsPath, out int skipped);

            Assert.Equal(0, skipped);
            Assert.Null(recNode.GetValue("precHash"));
        }
    }
}
