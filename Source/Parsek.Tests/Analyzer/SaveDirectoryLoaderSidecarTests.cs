using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Sidecar hydration + probe-capture tests (task 1.2). These write real .prec /
    // _vessel.craft sidecars via ScenarioWriter, then corrupt them to prove the
    // loader records a LoadFault (never crashes) and captures schema into
    // SidecarSchema. Sequential because they touch RecordingStore statics.
    [Collection("Sequential")]
    public class SaveDirectoryLoaderSidecarTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public SaveDirectoryLoaderSidecarTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-sidecar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = prevSuppress;
            ParsekLog.SuppressLogging = true;

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private string NewSaveDir(string name)
        {
            string dir = Path.Combine(tempDir, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteSave(string saveDir, ScenarioWriter writer)
        {
            string scenarioText = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO", 1);
            string save =
                "GAME\n{\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                scenarioText +
                "}\n";
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"), save);
        }

        private static RecordingBuilder ValidBuilder(string id)
        {
            return new RecordingBuilder("Sidecar Craft")
                .WithRecordingId(id)
                .AddPoint(100, 0, 0, 1000)
                .AddPoint(110, 0.01, 0.02, 1500)
                .WithVesselSnapshot(VesselSnapshotBuilder.FleaRocket("Sidecar Craft", "Jeb", pid: 7001));
        }

        private static string PrecPath(string saveDir, string id) =>
            Path.Combine(saveDir, "Parsek", "Recordings", id + ".prec");

        private static string VesselPath(string saveDir, string id) =>
            Path.Combine(saveDir, "Parsek", "Recordings", id + "_vessel.craft");

        private static CelestialBody NullResolver(string name) => null;

        // Guards: a valid current-generation sidecar set hydrates the trajectory into
        // the recording and captures (generation=4, formatVersion=1) into
        // SidecarSchema for INV5, with zero faults.
        [Fact]
        public void ValidSidecars_HydrateTrajectory_AndCaptureSchema()
        {
            string saveDir = NewSaveDir("valid");
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("valid0"));
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Empty(model.LoadFaults);
            Recording rec = model.Recordings.Single();
            Assert.Equal(2, rec.Points.Count);
            Assert.NotNull(rec.VesselSnapshot);
            Assert.True(model.SidecarSchema.ContainsKey("valid0"));
            Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration, model.SidecarSchema["valid0"].Generation);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, model.SidecarSchema["valid0"].FormatVersion);
        }

        // Guards: a truncated .prec produces a trajectory LoadFault and no crash
        // (design edge case 1). A crash instead of a finding is itself a bug.
        [Fact]
        public void TruncatedPrec_ProducesTrajectoryFault_NoCrash()
        {
            string saveDir = NewSaveDir("truncated");
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("trunc0"));
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);

            string prec = PrecPath(saveDir, "trunc0");
            byte[] bytes = File.ReadAllBytes(prec);
            Array.Resize(ref bytes, 6); // keep the magic, drop the rest
            File.WriteAllBytes(prec, bytes);

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Contains(model.LoadFaults, f => f.FileKind == "trajectory" && f.RecordingId == "trunc0");
        }

        // Guards: a text-format .prec (pre-v0-reset corpus) records the exact
        // production reject reason (design edge case 2), so triage greps match KSP.log.
        [Fact]
        public void TextFormatPrec_ReasonTextSidecarUnsupported()
        {
            string saveDir = NewSaveDir("textprec");
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("text0"));
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);

            var textNode = new ConfigNode("PARSEK_RECORDING");
            textNode.AddValue("version",
                RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            textNode.AddValue("recordingId", "text0");
            textNode.AddValue("sidecarEpoch", "1");
            textNode.Save(PrecPath(saveDir, "text0"));

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Contains(model.LoadFaults, f =>
                f.FileKind == "trajectory" &&
                f.RecordingId == "text0" &&
                f.Reason == "text-sidecar-unsupported");
        }

        // Guards: a pre-reset binary magic .prec records the pre-reset reject reason
        // (design edge case 3).
        [Fact]
        public void PreResetMagicPrec_ProducesTrajectoryFault()
        {
            string saveDir = NewSaveDir("prereset");
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("pre0"));
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);

            // "PRKB" pre-reset magic tag, then filler.
            var bytes = new List<byte> { (byte)'P', (byte)'R', (byte)'K', (byte)'B' };
            bytes.AddRange(new byte[] { 0, 0, 0, 1 });
            File.WriteAllBytes(PrecPath(saveDir, "pre0"), bytes.ToArray());

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Contains(model.LoadFaults, f =>
                f.FileKind == "trajectory" &&
                f.RecordingId == "pre0" &&
                f.Reason == "magic-mismatch");
        }

        // Guards: a corrupt vessel snapshot produces a snapshot LoadFault and leaves
        // the recording's VesselSnapshot null (design edge case 23), never a crash.
        [Fact]
        public void CorruptSnapshot_ProducesSnapshotFault_NoCrash()
        {
            string saveDir = NewSaveDir("badsnapshot");
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("snap0"));
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);

            string vessel = VesselPath(saveDir, "snap0");
            byte[] bytes = File.ReadAllBytes(vessel);
            Array.Resize(ref bytes, bytes.Length - 4); // truncate the deflate payload
            File.WriteAllBytes(vessel, bytes);

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Contains(model.LoadFaults, f => f.FileKind == "snapshot" && f.RecordingId == "snap0");
            Assert.Null(model.Recordings.Single().VesselSnapshot);
        }

        // Guards: a recording with no snapshot sidecar on disk is not a fault
        // (snapshots are optional for destroyed / showcase recordings).
        [Fact]
        public void MissingSnapshot_IsNotAFault()
        {
            string saveDir = NewSaveDir("nosnapshot");
            var noSnapshot = new RecordingBuilder("Trajectory Only")
                .WithRecordingId("nosnap0")
                .AddPoint(100, 0, 0, 1000)
                .AddPoint(110, 0.01, 0.02, 1500);
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(noSnapshot);
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.DoesNotContain(model.LoadFaults, f => f.FileKind == "snapshot");
        }

        // Guards: an orphan .prec (no tree recording references it) is inventoried in
        // SidecarSchema under an id absent from Recordings, so INV5 can flag it later.
        [Fact]
        public void OrphanSidecar_IsInventoriedInSidecarSchema()
        {
            string saveDir = NewSaveDir("orphan");

            var referenced = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("ref0"));
            WriteSave(saveDir, referenced);
            referenced.WriteSidecarFiles(saveDir);

            // A stray sidecar whose tree is never written into persistent.sfs.
            var orphanWriter = new ScenarioWriter().WithV3Format().AddRecordingAsTree(ValidBuilder("orphan0"));
            orphanWriter.WriteSidecarFiles(saveDir);

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            var recordingIds = model.Recordings.Select(r => r.RecordingId).ToList();
            Assert.Contains("ref0", recordingIds);
            Assert.DoesNotContain("orphan0", recordingIds);
            Assert.True(model.SidecarSchema.ContainsKey("orphan0"),
                "orphan .prec must be inventoried in SidecarSchema");
        }
    }
}
