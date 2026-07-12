using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV9 rewind-save + id validation. Loader-scoped fixtures write real
    // Parsek/Saves/<id>.sfs rewind quicksaves (the RewindSaveFileName system, NOT
    // the rp_* RewindPoints system); the traversal-id case is pure in-memory to
    // prove validation fires without a filesystem touch. Sequential because the
    // loader touches RecordingStore statics. Each test names the regression.
    [Collection("Sequential")]
    public class Inv9RewindPointTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public Inv9RewindPointTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-inv9-" + Guid.NewGuid().ToString("N"));
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

        private static void WriteSave(string saveDir, ScenarioWriter writer)
        {
            string scenarioText = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO", 1);
            string save =
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" + scenarioText + "}\n";
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"), save);
        }

        // Rewind saves live at Parsek/Saves/<id>.sfs (BuildRewindSaveRelativePath),
        // NOT Parsek/RewindPoints/. Write a valid ConfigNode there.
        private static void WriteRewindSave(string saveDir, string rewindId)
        {
            string dir = Path.Combine(saveDir, "Parsek", "Saves");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, rewindId + ".sfs"),
                "GAME\n{\n\tversion = 1.12.5\n}\n");
        }

        private string BuildSave(string name, RecordingBuilder builder)
        {
            string saveDir = Path.Combine(tempDir, name);
            Directory.CreateDirectory(saveDir);
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(builder);
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);
            return saveDir;
        }

        private static List<Finding> Run(string saveDir)
        {
            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, name => null);
            return new Inv9RewindPoint().Evaluate(model).ToList();
        }

        // Guards (directory-fix regression): a valid rewind id whose
        // Parsek/Saves/<id>.sfs exists and parses -> zero INV9 findings. Fails if
        // the rule reverts to probing Parsek/RewindPoints/ (the rp_* system), which
        // flagged every present rewind save as missing (the c1/l2/test-career/mun
        // false-positive class this fix removes).
        [Fact]
        public void ValidRewindSave_PresentInSavesDir_NoFindings()
        {
            string saveDir = BuildSave("valid",
                new RecordingBuilder("RP Craft").WithRecordingId("rp0")
                    .AddPoint(100, 0, 0, 1000).WithRewindSave("parsek_rw_rpfile0"));
            WriteRewindSave(saveDir, "parsek_rw_rpfile0");

            Assert.Empty(Run(saveDir));
        }

        // Guards (edge case 19): a RewindPoint id with a path-traversal sequence ->
        // FAIL via ValidateRecordingId, emitted from a pure in-memory model whose
        // SaveDirectory is a nonexistent path, so the escaped path is never touched.
        // Fails if a traversal id reaches the filesystem (security regression).
        [Fact]
        public void TraversalRewindId_Fails_WithoutFsAccess()
        {
            var model = new AnalyzerModel
            {
                SaveName = "trav",
                SaveDirectory = Path.Combine(tempDir, "does-not-exist"),
                Recordings = new List<Recording>
                {
                    new Recording { RecordingId = "rec0", RewindSaveFileName = "../evil" },
                },
            };

            List<Finding> findings = new Inv9RewindPoint().Evaluate(model).ToList();

            Finding fail = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("badid", fail.Message);
            Assert.Contains("../evil", fail.Message);
        }

        // Guards (severity-tuning regression): a recording referencing an absent
        // rewind save -> WARN, NOT FAIL. A missing rewind save is a dangling
        // reference, not proven corruption (RecordingStore.DeleteRecordingFiles
        // deletes shared rewind saves without reference-counting siblings; sealed
        // recordings can no longer rewind; production treats a missing hint as
        // benign). Fails if a dangling rewind reference reds the run (the s15 /
        // orbital-supply-route class), or if it is silently dropped.
        [Fact]
        public void MissingRewindSave_Warns_NotFails()
        {
            string saveDir = BuildSave("missing",
                new RecordingBuilder("RP Craft").WithRecordingId("rp1")
                    .AddPoint(100, 0, 0, 1000).WithRewindSave("parsek_rw_missing"));

            List<Finding> findings = Run(saveDir);

            Assert.Empty(findings.Where(f => f.Level == VerdictLevel.Fail));
            Finding warn = Assert.Single(findings, f => f.Level == VerdictLevel.Warn);
            Assert.Contains("missing-rewind-save", warn.Message);
        }

        // Guards (F1 severity-split): a CommittedProvisional recording (an OPEN /
        // still-rewindable slot) whose own rewind save is missing -> FAIL, distinct
        // token "missing-rewind-save-provisional". The blanket WARN downgrade blurred
        // exactly this class: this recording can still be rewound and a real
        // Rewind-to-Separation would fail to find its quicksave. In-memory model with
        // a real (empty) SaveDirectory so the file is genuinely absent; the rewind id
        // validates, so it reaches the existence check.
        [Fact]
        public void MissingRewindSave_Provisional_Fails_DistinctToken()
        {
            var model = new AnalyzerModel
            {
                SaveName = "prov-dangling",
                SaveDirectory = tempDir,
                Recordings = new List<Recording>
                {
                    new Recording
                    {
                        RecordingId = "recProv",
                        RewindSaveFileName = "parsek_rw_prov",
                        MergeState = MergeState.CommittedProvisional,
                    },
                },
            };

            List<Finding> findings = new Inv9RewindPoint().Evaluate(model).ToList();

            Finding fail = Assert.Single(findings, f => f.Level == VerdictLevel.Fail);
            Assert.Contains("missing-rewind-save-provisional", fail.Message);
            Assert.Contains("recProv", fail.Message);
            Assert.Empty(findings.Where(f => f.Level == VerdictLevel.Warn));
        }

        // Guards (F1 severity-split): an Immutable (sealed / no-longer-rewindable)
        // recording whose rewind save is missing stays WARN, NOT FAIL. Same in-memory
        // shape as the provisional case but sealed -> the WARN token, so a genuine
        // dangling reference on canon never reds the run.
        [Fact]
        public void MissingRewindSave_Immutable_Warns_NotFails()
        {
            var model = new AnalyzerModel
            {
                SaveName = "immutable-dangling",
                SaveDirectory = tempDir,
                Recordings = new List<Recording>
                {
                    new Recording
                    {
                        RecordingId = "recSealed",
                        RewindSaveFileName = "parsek_rw_sealed",
                        MergeState = MergeState.Immutable,
                    },
                },
            };

            List<Finding> findings = new Inv9RewindPoint().Evaluate(model).ToList();

            Finding warn = Assert.Single(findings, f => f.Level == VerdictLevel.Warn);
            Assert.Contains("missing-rewind-save", warn.Message);
            Assert.DoesNotContain("provisional", warn.Message);
            Assert.Empty(findings.Where(f => f.Level == VerdictLevel.Fail));
        }

        // Guards (F1 severity-split): a CommittedProvisional recording whose rewind
        // save IS present and parses -> zero findings. The FAIL is scoped to the
        // MISSING file, not to the provisional state itself.
        [Fact]
        public void PresentRewindSave_Provisional_NoFindings()
        {
            WriteRewindSave(tempDir, "parsek_rw_provok");
            var model = new AnalyzerModel
            {
                SaveName = "prov-present",
                SaveDirectory = tempDir,
                Recordings = new List<Recording>
                {
                    new Recording
                    {
                        RecordingId = "recProvOk",
                        RewindSaveFileName = "parsek_rw_provok",
                        MergeState = MergeState.CommittedProvisional,
                    },
                },
            };

            Assert.Empty(new Inv9RewindPoint().Evaluate(model).ToList());
        }

        // Guards (orphan-retarget regression): an unreferenced parsek_rw_*.sfs on
        // disk is an EXPECTED benign state (ParsekFlight.cs), reported as a single
        // per-save INFO inventory line, never a WARN/FAIL. Fails if the orphan scan
        // reverts to Parsek/RewindPoints/ (which false-flagged live rp_* RewindPoints
        // the model never loads), or promotes the benign orphan to WARN.
        [Fact]
        public void OrphanRewindSave_ReportsInfoInventory_NotWarn()
        {
            string saveDir = BuildSave("orphan",
                new RecordingBuilder("RP Craft").WithRecordingId("rp2").AddPoint(100, 0, 0, 1000));
            WriteRewindSave(saveDir, "parsek_rw_stray");

            List<Finding> findings = Run(saveDir);

            Assert.Empty(findings.Where(f => f.Level == VerdictLevel.Fail || f.Level == VerdictLevel.Warn));
            Finding info = Assert.Single(findings, f => f.Level == VerdictLevel.Info);
            Assert.Contains("orphan-rewind-saves", info.Message);
            Assert.Contains("count=1", info.Message);
        }
    }
}
