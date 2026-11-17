using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The one-shot writer of the frozen format golden <c>Fixtures/FormatGolden/v1/</c>. It runs
    /// the production save writers in <c>ParsekScenario.OnSave</c>'s phase order over
    /// generator-built state through <see cref="SaveWriterDriver"/> (the real OnSave needs a live
    /// game, so each phase is called directly) and writes a KSP-shaped save folder.
    /// It is SKIPPED unless <c>PARSEK_WRITE_FORMAT_GOLDEN=v1</c>, so a normal run never rewrites
    /// the committed files; the golden is frozen once written (see FormatGolden/README.md).
    /// </summary>
    [Collection("Sequential")]
    public class FormatGoldenV1GeneratorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;
        private readonly string staging;

        public FormatGoldenV1GeneratorTests()
        {
            originalScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            SaveWriterDriver.ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            staging = Path.Combine(Path.GetTempPath(), "parsek-format-golden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            SaveWriterDriver.ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            HighLogic.LoadedScene = originalScene;
            try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Set PARSEK_WRITE_FORMAT_GOLDEN=v1 to write the v1 golden (once; it is frozen).</summary>
        internal sealed class WriteFormatGoldenV1FactAttribute : FactAttribute
        {
            public WriteFormatGoldenV1FactAttribute()
            {
                if (Environment.GetEnvironmentVariable(FormatGolden.WriteEnvVar) != "v1")
                    Skip = "set " + FormatGolden.WriteEnvVar + "=v1 to write Fixtures/FormatGolden/v1 "
                           + "(frozen once written; a format bump adds a new vN folder instead)";
            }
        }

        // ---------------------------------------------------------------- the writer

        [Trait("Category", "Manual")]
        [WriteFormatGoldenV1Fact]
        public void WriteV1()
        {
            string repo = SyntheticRecordingTests.ResolveProjectRoot();
            RecordingPaths.SaveRootOverrideForTesting = staging;

            ParsekScenario scenario = SaveWriterDriver.BuildState();
            ConfigNode parsek = SaveWriterDriver.RunSaveWriters(scenario);

            // The KSP save around the PARSEK node: a stock career save (no Parsek footprint).
            string basePath = Path.Combine(repo, "harness", "fixtures", "saves", "fresh-career", "persistent.sfs");
            ConfigNode baseRoot = ConfigNode.Load(basePath);
            Assert.NotNull(baseRoot);
            ConfigNode game = baseRoot.GetNode("GAME");
            Assert.NotNull(game);
            Assert.Null(game.GetNodes("SCENARIO").FirstOrDefault(s => s.GetValue("name") == "ParsekScenario"));
            game.AddNode(parsek);
            string sfs = Path.Combine(staging, "persistent.sfs");
            baseRoot.Save(sfs);

            // The rewind point's quicksave: a KSP save of the same world, as RewindPointAuthor
            // copies it under Parsek/RewindPoints/.
            string rpDir = Path.Combine(staging, "Parsek", "RewindPoints");
            Directory.CreateDirectory(rpDir);
            File.Copy(sfs, Path.Combine(rpDir, SaveWriterDriver.RewindPointId + ".sfs"));

            File.WriteAllText(Path.Combine(staging, FormatGolden.ManifestFileName),
                FormatGolden.BuildManifestText(SaveFormat.All));

            // Move into the committed tree.
            string target = FormatGolden.VersionDir(1);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            FormatGolden.CopyTree(staging, target);
            string careerSource = Path.Combine(repo, "Source", "Parsek.Tests", "Fixtures", "C2CareerPostFix");
            FormatGolden.CopyTree(careerSource, Path.Combine(target, FormatGolden.CareerFolderName));

            // Checksums: this folder's lines are rewritten, every other folder's kept verbatim.
            string root = FormatGolden.Root();
            string checksumsPath = Path.Combine(root, FormatGolden.ChecksumsFileName);
            var map = File.Exists(checksumsPath)
                ? FormatGolden.ReadChecksums(checksumsPath)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in map.Keys.Where(k => k.StartsWith("v1/", StringComparison.Ordinal)).ToList())
                map.Remove(key);
            foreach (string path in FormatGolden.FrozenFiles(target))
                map[FormatGolden.RelativeKey(root, path)] = FormatGolden.Sha256(path);
            File.WriteAllText(checksumsPath, FormatGolden.BuildChecksumsText(map));
        }
    }
}
