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
    ///
    /// <para>The load-and-start route lives there too: the one <c>onGameStatePostLoad</c> fire
    /// in the mod, the Space Center start in stock's Load-menu order (adopt, start scene,
    /// <c>UpdateScenarioModules</c>, then the <c>persistent.sfs</c> write that
    /// <c>SpaceCenterMain.Start</c> re-reads), and the <c>LoadGame</c> seam verb reaches it
    /// through its steps in its own order. The <c>GamePersistence.LoadGame(</c> count per file
    /// is <c>LoadReconcileWiringGateTests</c>' job.</para>
    /// </summary>
    public class NewSaveStarterWiringTests
    {
        private const string CreateCall = "GamePersistence.CreateNewGame(";
        private const string FireCall = "GameEvents.onGameNewStart.Fire(";
        private const string StarterPath = "LogIO/NewSaveStarter.cs";
        private const string ApplierPath = "TestCommands/ParsekTestCommandAddon.NewGame.cs";
        private const string SeamPath = "TestCommands/ParsekTestCommandAddon.cs";
        private const string PostLoadFireCall = "GameEvents.onGameStatePostLoad.Fire(";

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

        [Fact]
        public void PlayerLoadFire_AppearsOnlyInNewSaveStarter_Once()
        {
            string sourceRoot = Path.Combine(ResolveRepoRoot(), "Source", "Parsek");
            var fireSites = new List<string>();
            foreach (string path in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(sourceRoot.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.Ordinal)
                    || rel.StartsWith("obj/", StringComparison.Ordinal))
                    continue;
                string text = SourceScanText.StripCSharpComments(File.ReadAllText(path));
                for (int i = 0; i < Count(text, PostLoadFireCall); i++) fireSites.Add(rel);
            }

            Assert.Equal(new[] { StarterPath }, fireSites.ToArray());

            // And it sits inside FirePlayerLoad's own body: a fire moved into another step
            // (PrepareSpaceCenterStart) would fire on every SPACECENTER boot and never for a
            // player load on the other routes.
            string body = MethodBody(StarterPath, "internal static void FirePlayerLoad(Game game)");
            Assert.Equal(1, Count(body, "GameEvents.onGameStatePostLoad.Fire(game.config);"));
        }

        [Fact]
        public void SpaceCenterStart_AdoptsThenUpdatesThenWritesPersistent()
        {
            string body = Body(Prepared(StarterPath),
                "internal static string PrepareSpaceCenterStart(", "\n        }");
            string[] steps =
            {
                "HighLogic.CurrentGame = game;",
                "game.startScene = GameScenes.SPACECENTER;",
                "GamePersistence.UpdateScenarioModules(game);",
                "return GamePersistence.SaveGame(game, \"persistent\", saveFolder, SaveMode.OVERWRITE);",
            };
            AssertInOrder(body, steps, "PrepareSpaceCenterStart");
            // The caller starts the game after its own persist line; the step never does.
            Assert.Equal(0, Count(body, ".Start()"));
        }

        [Fact]
        public void LoadSave_PointsTheSaveFolderThenLoadsWithoutAPopup()
        {
            string body = Body(Prepared(StarterPath),
                "internal static Game LoadSave(", "\n        }");
            AssertInOrder(body, new[]
            {
                "if (!string.IsNullOrEmpty(saveFolder))",
                "HighLogic.SaveFolder = saveFolder;",
                "return GamePersistence.LoadGame(slotName, saveFolder, true, false);",
            }, "LoadSave");
        }

        [Fact]
        public void LoadGameVerb_RunsTheLoadRouteStepsInOrder()
        {
            string seam = Prepared(SeamPath);
            // The verb keeps no stock load call or post-load fire of its own.
            Assert.Equal(0, Count(seam, "GamePersistence.LoadGame("));
            Assert.Equal(0, Count(seam, PostLoadFireCall));

            string body = Body(seam, "private void LoadGameImpl(", "private void RunTestsImpl(");
            string[] calls =
            {
                "NewSaveStarter.LoadSave(save, name)",
                "if (playerLoad)",
                "NewSaveStarter.FirePlayerLoad(game);",
                "if (route == LoadRoute.NoVesselSpaceCenter)",
                "NewSaveStarter.PrepareSpaceCenterStart(game, save);",
                "ParsekLog.Info(Tag,",
                "game.Start();",
            };
            foreach (string call in new[] { calls[0], calls[2], calls[4] })
                Assert.Equal(1, Count(body, call));
            int from = body.IndexOf("if (route == LoadRoute.Failed)", StringComparison.Ordinal);
            Assert.True(from >= 0, "the failed-route refusal moved; this gate is vacuous");
            Assert.True(body.IndexOf(calls[0], StringComparison.Ordinal) < from,
                "the load runs before the route is decided");
            AssertInOrder(body.Substring(from), new[] { calls[1], calls[2], calls[3], calls[4], calls[5], calls[6] },
                "the LoadGame verb's SPACECENTER route");

            // The call sits inside the guard's own block, not merely after it.
            string masked = SourceScanText.StripCommentsAndMaskLiterals(
                File.ReadAllText(SourcePath(SeamPath)));
            int impl = masked.IndexOf("private void LoadGameImpl(", StringComparison.Ordinal);
            Assert.True(impl >= 0, "LoadGameImpl moved; this gate is vacuous");
            int fireCall = masked.IndexOf(calls[2], impl, StringComparison.Ordinal);
            int guard = masked.LastIndexOf(calls[1], fireCall, StringComparison.Ordinal);
            Assert.True(guard > impl, "the FirePlayerLoad call has no playerLoad guard in LoadGameImpl");
            int open = masked.IndexOf('{', guard);
            Assert.Equal(string.Empty, masked.Substring(guard + calls[1].Length,
                open - guard - calls[1].Length).Trim());
            List<int> stack = SourceScanText.OpenBlockStack(masked, fireCall);
            Assert.True(stack.Count > 0 && stack[stack.Count - 1] == open,
                "NewSaveStarter.FirePlayerLoad(game) sits outside the if (playerLoad) block");
        }

        private static void AssertInOrder(string text, string[] steps, string where)
        {
            int last = -1;
            foreach (string step in steps)
            {
                int at = text.IndexOf(step, last + 1, StringComparison.Ordinal);
                Assert.True(at > last, step + " missing or out of order in " + where);
                last = at;
            }
        }

        private static string Body(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, start + " not found; this gate is vacuous");
            int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to > from, end + " not found after " + start);
            return text.Substring(from, to - from);
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
            return SourceScanText.StripCSharpComments(File.ReadAllText(SourcePath(relativePath)));
        }

        private static string SourcePath(string relativePath)
        {
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source moved, this gate is vacuous: " + path);
            return path;
        }

        // The brace-matched body of the method declared by signature, read from the
        // comment-stripped, literal-masked source (a brace in a string cannot unbalance it).
        private static string MethodBody(string relativePath, string signature)
        {
            string masked = SourceScanText.StripCommentsAndMaskLiterals(
                File.ReadAllText(SourcePath(relativePath)));
            int at = masked.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " not found; this gate is vacuous");
            int open = masked.IndexOf('{', at + signature.Length);
            Assert.True(open > at, signature + " has no body");
            return SourceScanText.BraceMatchedBlock(masked, open);
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
