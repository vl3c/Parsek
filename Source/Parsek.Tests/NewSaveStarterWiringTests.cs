using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Stock's new-game calls have one home: <c>LogIO/NewSaveStarter.cs</c> holds the only
    /// <c>GamePersistence.CreateNewGame(</c> and the only <c>GameEvents.onGameNewStart.Fire(</c>
    /// in the mod, in stock's order before <c>.Start()</c>, and the <c>NewGame</c> applier
    /// reaches them through its three steps in order. Comments are stripped first: the
    /// <c>NewGame</c> remarks name these calls in prose.
    /// </summary>
    public class NewSaveStarterWiringTests
    {
        private const string CreateCall = "GamePersistence.CreateNewGame(";
        private const string FireCall = "GameEvents.onGameNewStart.Fire(";
        private const string StarterPath = "LogIO/NewSaveStarter.cs";
        private const string ApplierPath = "TestCommands/ParsekTestCommandAddon.NewGame.cs";

        [Fact]
        public void StockNewGameCalls_AppearOnlyInNewSaveStarter_OnceEach()
        {
            string sourceRoot = Path.Combine(ResolveRepoRoot(), "Source", "Parsek");
            var createSites = new List<string>();
            var fireSites = new List<string>();
            foreach (string path in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(sourceRoot.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.Ordinal)
                    || rel.StartsWith("obj/", StringComparison.Ordinal))
                    continue;
                string text = SourceScanText.StripCSharpComments(File.ReadAllText(path));
                for (int i = 0; i < Count(text, CreateCall); i++) createSites.Add(rel);
                for (int i = 0; i < Count(text, FireCall); i++) fireSites.Add(rel);
            }

            Assert.Equal(new[] { StarterPath }, createSites.ToArray());
            Assert.Equal(new[] { StarterPath }, fireSites.ToArray());
        }

        [Fact]
        public void NewSaveStarter_CreatesThenFiresThenStarts()
        {
            string text = Prepared(StarterPath);
            int create = text.IndexOf(CreateCall, StringComparison.Ordinal);
            int fire = text.IndexOf(FireCall, StringComparison.Ordinal);
            int start = text.IndexOf(".Start()", StringComparison.Ordinal);
            Assert.True(create >= 0 && fire > create && start > fire,
                "NewSaveStarter must run CreateNewGame, onGameNewStart.Fire, Game.Start in "
                + "stock's order: create=" + create + " fire=" + fire + " start=" + start);
            Assert.Equal(1, Count(text, ".Start()"));
        }

        [Fact]
        public void NewGameApplier_RunsTheThreeStepsInOrder()
        {
            string text = Prepared(ApplierPath);
            string[] calls =
            {
                "NewSaveStarter.ResolveSaveDirectory(",
                "NewSaveStarter.DeleteSaveFolderLikeStockOverwrite(",
                "NewSaveStarter.CreateGame(",
                "NewSaveStarter.StartGame(",
            };
            int last = -1;
            foreach (string call in calls)
            {
                Assert.Equal(1, Count(text, call));
                int at = text.IndexOf(call, StringComparison.Ordinal);
                Assert.True(at > last, call + " runs out of order in the NewGame applier");
                last = at;
            }
            // The applier keeps no stock new-game call or delete of its own.
            Assert.Equal(0, Count(text, ".Start()"));
            Assert.Equal(0, Count(text, "Directory.Delete("));
        }

        private static int Count(string text, string needle)
        {
            int n = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static string Prepared(string relativePath)
        {
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source moved, this gate is vacuous: " + path);
            return SourceScanText.StripCSharpComments(File.ReadAllText(path));
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
