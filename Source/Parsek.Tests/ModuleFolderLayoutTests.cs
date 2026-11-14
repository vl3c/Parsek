using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Floors for the source gates that list one folder of Source/Parsek. Every .cs file
    /// sits in its module's folder (Source/Parsek/&lt;Module&gt;/, scripts/arch/modules.toml),
    /// and a gate that reads a partial class lists the folder of its main file. A file left
    /// in the root, or a partial file filed in another folder than its main file, would be
    /// missing from such a listing and the gate would pass without reading it.
    /// </summary>
    public class ModuleFolderLayoutTests
    {
        private static string SourceRoot()
        {
            string root = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source dir not found at " + root);
            return root;
        }

        private static List<string> SourceFiles(string root)
        {
            var files = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                    continue;
                files.Add(rel);
            }
            return files;
        }

        [Fact]
        public void NoSourceFileSitsInTheRootOfSourceParsek()
        {
            string root = SourceRoot();
            List<string> files = SourceFiles(root);
            Assert.True(files.Count >= 1200,
                "module-folder floor: only " + files.Count + " .cs files under Source/Parsek");
            var rootFiles = files.Where(rel => rel.IndexOf('/') < 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(rootFiles.Count == 0,
                "every .cs file belongs in its module's folder, Source/Parsek/<Module>/ "
                + "(run scripts/refactor/move_to_module_folders.py): " + string.Join(", ", rootFiles));
        }

        // The partial families whose files a gate lists by folder, with the folder of the main
        // file and a floor on the partial files beside it (the count on 2026-11-14).
        [Theory]
        [InlineData("ParsekFlight", "Controllers", 17)]
        [InlineData("ParsekScenario", "Controllers", 26)]
        [InlineData("VesselSpawner", "Controllers", 8)]
        [InlineData("BackgroundRecorder", "Recording", 13)]
        [InlineData("FlightRecorder", "Recording", 15)]
        [InlineData("RewindInvoker", "Rewind", 9)]
        [InlineData("GhostPlaybackEngine", "Ghost", 12)]
        [InlineData("GhostMapPresence", "Ghost", 15)]
        [InlineData("KspStatePatcher", "GameActions", 10)]
        [InlineData("LedgerOrchestrator", "GameActions", 9)]
        public void EveryFileOfAPartialFamilySitsBesideItsMainFile(string stem, string folder, int minPartials)
        {
            string root = SourceRoot();
            var family = SourceFiles(root)
                .Where(rel =>
                {
                    string name = rel.Substring(rel.LastIndexOf('/') + 1);
                    return name == stem + ".cs"
                        || (name.StartsWith(stem + ".", StringComparison.Ordinal)
                            && name.EndsWith(".cs", StringComparison.Ordinal));
                })
                .OrderBy(rel => rel, StringComparer.Ordinal)
                .ToList();
            Assert.Contains(folder + "/" + stem + ".cs", family);
            var elsewhere = family.Where(rel => rel.Substring(0, rel.LastIndexOf('/') + 1) != folder + "/").ToList();
            Assert.True(elsewhere.Count == 0,
                stem + " partial files outside " + folder + "/: " + string.Join(", ", elsewhere));
            Assert.True(family.Count - 1 >= minPartials,
                stem + ": " + (family.Count - 1) + " partial files beside the main file, floor " + minPartials);
        }
    }
}
