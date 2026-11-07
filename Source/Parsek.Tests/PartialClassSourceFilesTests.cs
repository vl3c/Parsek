using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <see cref="PartialClassSourceFiles"/> must refuse a partial file its caller did not
    /// declare (the silent-skip it exists to prevent), refuse a declared file that is gone, and
    /// otherwise return the main file first and the read files in ordinal order. The three real
    /// readers are checked against the live tree as well.
    /// </summary>
    public class PartialClassSourceFilesTests : IDisposable
    {
        private readonly string dir;

        public PartialClassSourceFilesTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "parsek-partial-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (string name in new[] { "Widget.cs", "Widget.B.cs", "Widget.A.cs", "Widget.Old.cs", "WidgetOther.cs" })
                File.WriteAllText(Path.Combine(dir, name), "// " + name);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        [Fact]
        public void Paths_DeclaredSetMatches_MainFirstThenReadFilesOrdinal()
        {
            var paths = PartialClassSourceFiles.Paths(
                dir, "Widget.cs", new[] { "Widget.B.cs", "Widget.A.cs" }, new[] { "Widget.Old.cs" }, "TestReader");

            Assert.Equal(new[] { "Widget.cs", "Widget.A.cs", "Widget.B.cs" },
                paths.Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void Paths_UndeclaredPartialFile_ThrowsNamingIt()
        {
            File.WriteAllText(Path.Combine(dir, "Widget.New.cs"), "// new");

            var ex = Assert.Throws<InvalidOperationException>(() => PartialClassSourceFiles.Paths(
                dir, "Widget.cs", new[] { "Widget.A.cs", "Widget.B.cs" }, new[] { "Widget.Old.cs" }, "TestReader"));

            Assert.Contains("TestReader", ex.Message);
            Assert.Contains("Undeclared partial file(s): Widget.New.cs", ex.Message);
            Assert.Contains("SplitFileNames", ex.Message);
        }

        [Fact]
        public void Paths_DeclaredFileMissing_ThrowsNamingIt()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PartialClassSourceFiles.Paths(
                dir, "Widget.cs", new[] { "Widget.A.cs", "Widget.B.cs", "Widget.Gone.cs" }, new[] { "Widget.Old.cs" }, "TestReader"));

            Assert.Contains("Declared but not found: Widget.Gone.cs", ex.Message);
        }

        [Fact]
        public void Paths_MainFileMissing_ThrowsFileNotFound()
        {
            Assert.Throws<FileNotFoundException>(() => PartialClassSourceFiles.Paths(
                dir, "Gadget.cs", new string[0], new string[0], "TestReader"));
        }

        [Fact]
        public void RealReaders_MatchTheLiveTree()
        {
            string parsek = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek");

            Assert.Equal(1 + BackgroundRecorderSourceFiles.SplitFileNames.Length,
                BackgroundRecorderSourceFiles.Paths(parsek).Count);
            Assert.Equal(1 + GhostTrajectoryPolylineRendererSourceFiles.SplitFileNames.Length,
                GhostTrajectoryPolylineRendererSourceFiles.Paths(Path.Combine(parsek, "Display")).Count);
            Assert.Equal(1 + RecordingsTableUISourceFiles.SplitFileNames.Length,
                RecordingsTableUISourceFiles.Paths(Path.Combine(parsek, "UI")).Count);
            Assert.Equal(1 + VesselSpawnerSourceFiles.SplitFileNames.Length,
                VesselSpawnerSourceFiles.Paths(parsek).Count);
        }
    }
}
