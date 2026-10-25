using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The <c>NewGame</c> seam verb (scripted campaign program stage CS-0): the arg parse, the
    /// pre-exit gate order, the two-phase poll (main menu, then the Space Center on the new
    /// game), the delete's one kept entry, the payload and line shapes a lane pins, and the
    /// dispatch row.
    /// </summary>
    public class TestCommandNewGameTests
    {
        private static ParsedCommand Cmd(string line) => TestCommandParser.ParseLine(line, 1);

        // ----- Arg parse -----

        [Theory]
        [InlineData("SCIENCE_SANDBOX", (int)NewGameMode.ScienceSandbox)]
        [InlineData("CAREER", (int)NewGameMode.Career)]
        [InlineData("SANDBOX", (int)NewGameMode.Sandbox)]
        public void Parse_AcceptsEveryMode(string raw, int expectedMode)
        {
            NewGameMode expected = (NewGameMode)expectedMode;
            Assert.True(TestCommandNewGame.TryParse(raw, "Normal", "camp", out NewGameMode mode,
                out NewGamePreset preset, out string save, out string reason, out _));
            Assert.Equal(expected, mode);
            Assert.Equal(NewGamePreset.Normal, preset);
            Assert.Equal("camp", save);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("Easy", (int)NewGamePreset.Easy)]
        [InlineData("Normal", (int)NewGamePreset.Normal)]
        [InlineData("Moderate", (int)NewGamePreset.Moderate)]
        [InlineData("Hard", (int)NewGamePreset.Hard)]
        public void Parse_AcceptsEveryPreset(string raw, int expectedPreset)
        {
            NewGamePreset expected = (NewGamePreset)expectedPreset;
            Assert.True(TestCommandNewGame.TryParse("CAREER", raw, "camp", out _, out NewGamePreset preset,
                out _, out _, out _));
            Assert.Equal(expected, preset);
        }

        [Theory]
        [InlineData(null, "Normal", "camp", "newgame-mode-arg-missing")]
        [InlineData("career", "Normal", "camp", "newgame-mode-arg-invalid")]
        [InlineData("", "Normal", "camp", "newgame-mode-arg-invalid")]
        [InlineData("MISSION", "Normal", "camp", "newgame-mode-arg-invalid")]
        [InlineData("CAREER", null, "camp", "newgame-preset-arg-missing")]
        [InlineData("CAREER", "normal", "camp", "newgame-preset-arg-invalid")]
        [InlineData("CAREER", "Custom", "camp", "newgame-preset-arg-invalid")]
        [InlineData("CAREER", "Normal ", "camp", "newgame-preset-arg-invalid")]
        [InlineData("CAREER", "Normal", null, "newgame-save-arg-missing")]
        [InlineData("CAREER", "Normal", "", "newgame-save-arg-invalid")]
        [InlineData("CAREER", "Normal", "../x", "newgame-save-arg-invalid")]
        [InlineData("CAREER", "Normal", "a/b", "newgame-save-arg-invalid")]
        [InlineData("CAREER", "Normal", "a\\b", "newgame-save-arg-invalid")]
        [InlineData("CAREER", "Normal", " camp", "newgame-save-arg-invalid")]
        [InlineData("CAREER", "Normal", "#autoLOC_1", "newgame-save-arg-invalid")]
        public void Parse_FailsClosedInArgOrder(string mode, string preset, string save, string expected)
        {
            Assert.False(TestCommandNewGame.TryParse(mode, preset, save, out NewGameMode m,
                out NewGamePreset p, out string s, out string reason, out string detail));
            Assert.Equal(expected, reason);
            Assert.False(string.IsNullOrEmpty(detail));
            Assert.Equal(NewGameMode.None, m);
            Assert.Equal(NewGamePreset.None, p);
            Assert.Null(s);
        }

        [Fact]
        public void Parse_InvalidDetail_NamesTheValidSet()
        {
            TestCommandNewGame.TryParse("career", "Normal", "x", out _, out _, out _, out _, out string detail);
            Assert.Equal("mode=career valid=SCIENCE_SANDBOX,CAREER,SANDBOX", detail);
            TestCommandNewGame.TryParse("CAREER", "hard", "x", out _, out _, out _, out _, out detail);
            Assert.Equal("preset=hard valid=Easy,Normal,Moderate,Hard", detail);
        }

        [Theory]
        [InlineData("fresh-science", true)]
        [InlineData("camp stage 0", true)]
        [InlineData("a..b", false)]
        [InlineData(".", false)]
        [InlineData("x:y", false)]
        [InlineData("camp ", false)]
        [InlineData("   ", false)]
        public void SafeSaveName(string save, bool expected)
        {
            Assert.Equal(expected, TestCommandNewGame.IsSafeSaveName(save));
        }

        [Fact]
        public void Tokens_MatchTheStockEnumNames()
        {
            // The applier maps through the names, so the tokens must be the stock spellings.
            Assert.Equal(new[] { "SCIENCE_SANDBOX", "CAREER", "SANDBOX" }, TestCommandNewGame.ModeTokens);
            Assert.Equal(new[] { "Easy", "Normal", "Moderate", "Hard" }, TestCommandNewGame.PresetTokens);
            foreach (string mode in TestCommandNewGame.ModeTokens)
                Assert.True(System.Enum.IsDefined(typeof(Game.Modes), mode), mode);
            foreach (string preset in TestCommandNewGame.PresetTokens)
                Assert.True(System.Enum.IsDefined(typeof(GameParameters.Preset), preset), preset);
            Assert.Equal(NewGameMode.Career, TestCommandNewGame.ModeFromStockName("CAREER"));
            Assert.Equal(NewGameMode.None, TestCommandNewGame.ModeFromStockName("MISSION"));
            Assert.Equal(NewGamePreset.Hard, TestCommandNewGame.PresetFromStockName("Hard"));
            Assert.Equal(NewGamePreset.None, TestCommandNewGame.PresetFromStockName("Custom"));
            Assert.Equal("none", TestCommandNewGame.ModeToken(NewGameMode.None));
            Assert.Equal("none", TestCommandNewGame.PresetToken(NewGamePreset.None));
        }

        // ----- Gate -----

        private static string Gate(
            TestCommandScene scene = TestCommandScene.SpaceCenter, string loaded = "camp",
            string requested = "camp", bool canLeave = true, bool recorder = false,
            bool activeTree = false, bool pendingTree = false, bool mergeDialog = false,
            bool refly = false, bool switchSession = false, bool journal = false)
            => TestCommandNewGame.DecideGate(scene, loaded, requested, canLeave, recorder, activeTree,
                pendingTree, mergeDialog, refly, switchSession, journal);

        [Fact]
        public void Gate_QuiescentSpaceCenter_Proceeds()
        {
            Assert.Null(Gate());
        }

        [Theory]
        [InlineData((int)TestCommandScene.Flight)]
        [InlineData((int)TestCommandScene.MainMenu)]
        [InlineData((int)TestCommandScene.Editor)]
        [InlineData((int)TestCommandScene.TrackingStation)]
        public void Gate_OutsideTheSpaceCenter_RefusesWrongSceneFirst(int sceneValue)
        {
            TestCommandScene scene = (TestCommandScene)sceneValue;
            Assert.Equal("newgame-wrong-scene", Gate(scene: scene, loaded: "other", canLeave: false,
                recorder: true, pendingTree: true));
        }

        [Theory]
        [InlineData("Camp")]
        [InlineData("camp2")]
        [InlineData(null)]
        public void Gate_SaveMismatch_IsOrdinal(string loaded)
        {
            Assert.Equal("newgame-save-mismatch", Gate(loaded: loaded, canLeave: false));
        }

        [Fact]
        public void Gate_FollowsTheDeclaredOrder()
        {
            Assert.Equal("newgame-main-menu-locked", Gate(canLeave: false, recorder: true));
            Assert.Equal("newgame-recorder-live", Gate(recorder: true, activeTree: true));
            Assert.Equal("newgame-active-tree", Gate(activeTree: true, pendingTree: true));
            Assert.Equal("newgame-pending-tree", Gate(pendingTree: true, mergeDialog: true));
            Assert.Equal("newgame-merge-dialog-pending", Gate(mergeDialog: true, refly: true));
            Assert.Equal("newgame-refly-session", Gate(refly: true, switchSession: true));
            Assert.Equal("newgame-switch-segment-session", Gate(switchSession: true, journal: true));
            Assert.Equal("newgame-merge-journal", Gate(journal: true));
        }

        // ----- Reason tables -----

        [Fact]
        public void ReasonTables_MatchTheConstants_AndAreDisjoint()
        {
            var consts = typeof(TestCommandNewGame)
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.IsLiteral && f.Name.EndsWith("Reason"))
                .Select(f => (string)f.GetRawConstantValue())
                .ToList();
            var all = TestCommandNewGame.Reasons.Concat(TestCommandNewGame.ErrorReasons).ToList();
            Assert.Equal(consts.OrderBy(x => x), all.OrderBy(x => x));
            Assert.Empty(TestCommandNewGame.Reasons.Intersect(TestCommandNewGame.ErrorReasons));
            Assert.Equal(all.Count, all.Distinct().Count());
            foreach (string r in all)
                Assert.StartsWith("newgame-", r);
        }

        // ----- Delete, flag, pools -----

        [Theory]
        [InlineData("Ships", true)]
        [InlineData("ships", true)]
        [InlineData("Parsek", false)]
        [InlineData("Subassemblies", false)]
        [InlineData("Ships2", false)]
        public void Delete_KeepsOnlyShips(string entry, bool kept)
        {
            Assert.Equal(kept, TestCommandNewGame.ShouldPreserveSaveEntry(entry));
        }

        [Fact]
        public void Flag_UsesTheMenuDefault_ElseTheStockDefault()
        {
            Assert.Equal("Squad/Flags/kerbin", TestCommandNewGame.ResolveFlagUrl("Squad/Flags/kerbin"));
            Assert.Equal("Squad/Flags/default", TestCommandNewGame.ResolveFlagUrl(null));
            Assert.Equal("Squad/Flags/default", TestCommandNewGame.ResolveFlagUrl(""));
        }

        [Theory]
        [InlineData("CAREER", true, true, true, true)]
        [InlineData("CAREER", false, true, true, false)]
        [InlineData("CAREER", true, false, true, false)]
        [InlineData("CAREER", true, true, false, false)]
        [InlineData("SCIENCE_SANDBOX", false, true, false, true)]
        [InlineData("SCIENCE_SANDBOX", false, false, false, false)]
        [InlineData("SANDBOX", false, false, false, true)]
        public void Pools_PerMode(string modeToken, bool funding, bool rnd, bool rep, bool expected)
        {
            NewGameMode mode = TestCommandNewGame.ModeFromStockName(modeToken);
            Assert.Equal(expected, TestCommandNewGame.PoolsReady(mode, funding, rnd, rep));
        }

        // ----- Poll -----

        private static NewGamePollOutcome Exiting(TestCommandScene scene, bool menuUp, int polls, bool expired = false)
            => TestCommandNewGame.DecidePoll(NewGamePhase.Exiting, scene, menuUp, polls,
                false, false, false, false, false, false, 0, expired);

        private static NewGamePollOutcome Starting(
            TestCommandScene scene = TestCommandScene.SpaceCenter, bool game = true, bool scenario = true,
            bool save = true, bool mode = true, bool preset = true, bool pools = true, int readyPolls = 3,
            bool expired = false)
            => TestCommandNewGame.DecidePoll(NewGamePhase.Starting, scene, false, 0, game, scenario, save,
                mode, preset, pools, readyPolls, expired);

        [Fact]
        public void Poll_Exiting_CreatesOnlyOnASettledMenu()
        {
            Assert.Equal(NewGamePollOutcome.NotYet, Exiting(TestCommandScene.SpaceCenter, false, 0));
            Assert.Equal(NewGamePollOutcome.NotYet, Exiting(TestCommandScene.MainMenu, false, 9));
            Assert.Equal(NewGamePollOutcome.NotYet,
                Exiting(TestCommandScene.MainMenu, true, TestCommandNewGame.MinMenuSettlePolls - 1));
            Assert.Equal(NewGamePollOutcome.CreateNow,
                Exiting(TestCommandScene.MainMenu, true, TestCommandNewGame.MinMenuSettlePolls));
            // A ready menu wins over an expired budget on the same poll.
            Assert.Equal(NewGamePollOutcome.CreateNow,
                Exiting(TestCommandScene.MainMenu, true, TestCommandNewGame.MinMenuSettlePolls, expired: true));
            Assert.Equal(NewGamePollOutcome.ExitTimedOut, Exiting(TestCommandScene.SpaceCenter, false, 0, expired: true));
        }

        [Fact]
        public void Poll_Starting_ReadyNeedsEveryConjunctAndTheHeldPolls()
        {
            Assert.Equal(NewGamePollOutcome.Ready, Starting());
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(readyPolls: TestCommandNewGame.MinReadyPolls - 1));
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(pools: false));
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(scenario: false));
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(game: false));
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(save: false));
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(scene: TestCommandScene.Flight));
            Assert.Equal(NewGamePollOutcome.StartTimedOut, Starting(pools: false, expired: true));
        }

        [Fact]
        public void Poll_Starting_MenuIsTheFastFailure_AndMismatchIsTyped()
        {
            Assert.Equal(NewGamePollOutcome.ReturnedToMenu, Starting(scene: TestCommandScene.MainMenu, game: false));
            Assert.Equal(NewGamePollOutcome.Mismatch, Starting(mode: false, readyPolls: 0));
            Assert.Equal(NewGamePollOutcome.Mismatch, Starting(preset: false, readyPolls: 0));
            // A mismatch is only read on the requested save with Parsek loaded.
            Assert.Equal(NewGamePollOutcome.NotYet, Starting(mode: false, save: false, readyPolls: 0));
        }

        // ----- Payload and lines -----

        [Fact]
        public void Payload_KeySetDependsOnTheModeAlone()
        {
            var career = TestCommandNewGame.BuildCompletePayload(NewGameMode.Career, NewGamePreset.Normal,
                "camp", "SPACECENTER", 12345, 25000.0, 0.0, 0.0);
            Assert.Equal(new[] { "mode", "preset", "save", "scene", "seed", "funds", "science", "rep" },
                career.Select(kv => kv.Key).ToArray());
            var science = TestCommandNewGame.BuildCompletePayload(NewGameMode.ScienceSandbox, NewGamePreset.Hard,
                "camp", "SPACECENTER", 7, null, 0.0, null);
            Assert.Equal(new[] { "mode", "preset", "save", "scene", "seed", "science" },
                science.Select(kv => kv.Key).ToArray());
            var sandbox = TestCommandNewGame.BuildCompletePayload(NewGameMode.Sandbox, NewGamePreset.Easy,
                "camp", "SPACECENTER", 7, 1.0, 1.0, 1.0);
            Assert.Equal(new[] { "mode", "preset", "save", "scene", "seed" },
                sandbox.Select(kv => kv.Key).ToArray());
            var missing = TestCommandNewGame.BuildCompletePayload(NewGameMode.Career, NewGamePreset.Normal,
                "camp", "SPACECENTER", 7, null, null, null);
            Assert.Equal("-", missing.Single(kv => kv.Key == "funds").Value);
        }

        [Fact]
        public void CompleteLine_IsStableAndInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var payload = TestCommandNewGame.BuildCompletePayload(NewGameMode.Career, NewGamePreset.Normal,
                    "camp", "SPACECENTER", -42, 25000.0, 0.5, 1.25);
                Assert.Equal(
                    "newgame complete mode=CAREER preset=Normal save=camp scene=SPACECENTER seed=-42 "
                    + "funds=25000 science=0.5 rep=1.25 elapsed=12.3s",
                    TestCommandNewGame.FormatCompleteLine(payload, 12.34));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void StartAndCreateLines()
        {
            Assert.StartsWith("newgame start mode=SCIENCE_SANDBOX preset=Normal save=camp scene=SPACECENTER",
                TestCommandNewGame.FormatStartLine(NewGameMode.ScienceSandbox, NewGamePreset.Normal, "camp", "SPACECENTER"));
            Assert.Equal(
                "newgame create mode=CAREER preset=Hard save=camp flag=Squad/Flags/default deletedFiles=12 "
                + "deletedDirs=3 preserved=Ships",
                TestCommandNewGame.FormatCreateLine(NewGameMode.Career, NewGamePreset.Hard, "camp",
                    "Squad/Flags/default", 12, 3, true));
            Assert.EndsWith("preserved=none",
                TestCommandNewGame.FormatCreateLine(NewGameMode.Career, NewGamePreset.Hard, "camp", "f", 0, 1, false));
        }

        // ----- Dispatch -----

        [Fact]
        public void Dispatch_GameNotLoaded_Defers()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=NewGame mode=CAREER preset=Normal save=camp"),
                new DispatchState { Scene = TestCommandScene.MainMenu });
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("game-not-loaded", r.Reason);
        }

        [Fact]
        public void Dispatch_LoadInFlight_Rejects()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=NewGame mode=CAREER preset=Normal save=camp"),
                new DispatchState
                {
                    Scene = TestCommandScene.SpaceCenter, GameLoaded = true, SettingsPresent = true,
                    LoadInFlight = true,
                });
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_AtTheSpaceCenter_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=NewGame mode=CAREER preset=Normal save=camp"),
                new DispatchState { Scene = TestCommandScene.SpaceCenter, GameLoaded = true, SettingsPresent = true });
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Budget_IsTheLoadGameSize_AndTheVerbMutates()
        {
            Assert.Equal(300.0, DeferralBudget.BudgetSeconds("NewGame"));
            Assert.Equal(DeferralBudget.LoadGameSeconds, DeferralBudget.BudgetSeconds("NewGame"));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb("NewGame"));
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify("NewGame"));
        }
    }
}
