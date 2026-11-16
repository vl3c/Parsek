using System;
using System.Collections.Generic;
using System.IO;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The delete step of <see cref="NewSaveStarter"/> on a real temp folder: what it removes,
    /// what it keeps, the counts it returns and its one log line. The create and start steps
    /// are stock calls (live only); <see cref="NewSaveStarterWiringTests"/> pins where they are.
    /// </summary>
    [Collection("Sequential")]
    public class NewSaveStarterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string root;

        public NewSaveStarterTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            root = Path.Combine(Path.GetTempPath(), "parsek-newsave-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(root, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string Touch(params string[] parts)
        {
            string path = Path.Combine(root, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "x");
            return path;
        }

        [Fact]
        public void Delete_RemovesFilesNestedDirsAndTheFolder_AndReturnsCounts()
        {
            string save = Path.Combine(root, "camp");
            Touch("camp", "persistent.sfs");
            Touch("camp", "quicksave.sfs");
            Touch("camp", "Parsek", "Recordings", "a.prec");
            Touch("camp", "Parsek", "Recordings", "b.prec");
            Touch("camp", "Parsek", "GameState", "events.pgse");
            Directory.CreateDirectory(Path.Combine(save, "Empty"));

            var counts = new NewSaveStarter.DeleteResult();
            NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(
                save, TestCommandNewGame.ShouldPreserveSaveEntry, counts);

            Assert.False(Directory.Exists(save));
            Assert.Equal(5, counts.Files);
            // Parsek, Parsek/Recordings, Parsek/GameState, Empty, and the save folder itself.
            Assert.Equal(5, counts.Dirs);
            Assert.False(counts.Preserved);
            Assert.Contains(logLines, l => l.Contains("[NewSave]")
                && l.Contains("deleted save folder path=" + save)
                && l.Contains("files=5 dirs=5 preserved=false"));
        }

        [Fact]
        public void Delete_KeepsTheShipsFolderWhole_AndTheSaveFolderItself()
        {
            string save = Path.Combine(root, "camp");
            Touch("camp", "persistent.sfs");
            string craft = Touch("camp", "Ships", "VAB", "Rocket.craft");
            Touch("camp", "Parsek", "Recordings", "a.prec");

            var counts = new NewSaveStarter.DeleteResult();
            NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(
                save, TestCommandNewGame.ShouldPreserveSaveEntry, counts);

            Assert.True(Directory.Exists(save));
            Assert.True(File.Exists(craft));
            Assert.False(File.Exists(Path.Combine(save, "persistent.sfs")));
            Assert.False(Directory.Exists(Path.Combine(save, "Parsek")));
            Assert.Equal(2, counts.Files);
            Assert.Equal(2, counts.Dirs);
            Assert.True(counts.Preserved);
            Assert.Equal(new[] { "Ships" },
                Array.ConvertAll(Directory.GetFileSystemEntries(save), Path.GetFileName));
            Assert.Contains(logLines, l => l.Contains("[NewSave]") && l.Contains("preserved=true"));
        }

        [Fact]
        public void Delete_ClearsReadOnlyAttributesBeforeDeleting()
        {
            string save = Path.Combine(root, "camp");
            string top = Touch("camp", "persistent.sfs");
            string nested = Touch("camp", "Parsek", "locked.prec");
            File.SetAttributes(top, FileAttributes.ReadOnly);
            File.SetAttributes(nested, FileAttributes.ReadOnly);

            var counts = new NewSaveStarter.DeleteResult();
            NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(
                save, TestCommandNewGame.ShouldPreserveSaveEntry, counts);

            Assert.False(Directory.Exists(save));
            Assert.Equal(2, counts.Files);
            Assert.Equal(2, counts.Dirs);
        }

        [Fact]
        public void Delete_MissingFolderIsANoOp()
        {
            string save = Path.Combine(root, "never-made");

            var counts = new NewSaveStarter.DeleteResult();
            NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(
                save, TestCommandNewGame.ShouldPreserveSaveEntry, counts);

            Assert.Equal(0, counts.Files);
            Assert.Equal(0, counts.Dirs);
            Assert.False(counts.Preserved);
            Assert.False(Directory.Exists(save));
            Assert.Contains(logLines, l => l.Contains("[NewSave]")
                && l.Contains("delete skipped: no folder path=" + save));
        }

        [Fact]
        public void Delete_KeepsOnlyTopLevelEntries_ANestedShipsFolderGoes()
        {
            string save = Path.Combine(root, "camp");
            Touch("camp", "Parsek", "Ships", "x.craft");

            var counts = new NewSaveStarter.DeleteResult();
            NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(
                save, TestCommandNewGame.ShouldPreserveSaveEntry, counts);

            Assert.False(Directory.Exists(save));
            Assert.Equal(1, counts.Files);
            Assert.Equal(3, counts.Dirs);
            Assert.False(counts.Preserved);
        }

        [Fact]
        public void Delete_NullPredicateKeepsNothing()
        {
            string save = Path.Combine(root, "camp");
            Touch("camp", "Ships", "VAB", "Rocket.craft");

            var counts = new NewSaveStarter.DeleteResult();
            NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(save, null, counts);

            Assert.False(Directory.Exists(save));
            Assert.Equal(1, counts.Files);
            Assert.Equal(3, counts.Dirs);
            Assert.False(counts.Preserved);
        }

        [Fact]
        public void Delete_RequiresACountsObject()
        {
            Assert.Throws<ArgumentNullException>(() =>
                NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(
                    Path.Combine(root, "camp"), TestCommandNewGame.ShouldPreserveSaveEntry, null));
        }
    }
}
