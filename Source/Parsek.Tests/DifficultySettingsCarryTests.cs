using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q1 2026-11-04: Parsek's time travel (Rewind to Launch, Warp to game start,
    /// Re-Fly, Discard Re-fly) keeps the player's current difficulty settings instead of the
    /// ones saved in the rewind save.
    /// </summary>
    public class DifficultySettingsCarryTests
    {
        private static ConfigNode Params(float funds, bool allowNegative, string extra = null)
        {
            var root = new ConfigNode("PARAMETERS");
            root.AddValue("preset", "Custom");
            var diff = root.AddNode("DIFFICULTY");
            diff.AddValue("MissingCrewsRespawn", "True");
            var career = root.AddNode("CAREER");
            career.AddValue("FundsGainMultiplier", funds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var adv = root.AddNode("AdvancedParams");
            adv.AddValue("AllowNegativeCurrency", allowNegative ? "True" : "False");
            if (extra != null)
                adv.AddValue(extra, "1");
            return root;
        }

        [Fact]
        public void DescribeChanges_NamesEveryDifferingLeaf_WithItsPath()
        {
            var changes = DifficultySettingsCarry.DescribeChanges(
                Params(1f, false, extra: "OnlySaved"), Params(1.5f, false, extra: null));
            Assert.Equal(new[]
            {
                "CAREER.FundsGainMultiplier: 1 -> 1.5",
                "AdvancedParams.OnlySaved: 1 -> -",
            }, changes);
        }

        [Fact]
        public void DescribeChanges_IdenticalNodes_IsEmpty()
        {
            Assert.Empty(DifficultySettingsCarry.DescribeChanges(Params(2f, true), Params(2f, true)));
        }

        [Fact]
        public void ApplyTo_NullGameOrNode_IsANoOp()
        {
            DifficultySettingsCarry.ApplyTo(null, Params(1f, false), "test");
        }

        // Each of Parsek's own time-travel loads captures the live parameters before its
        // LoadGame and installs them before the loaded game is written or started.
        [Theory]
        [InlineData("RewindLauncher.cs", "WriteRewindGameAsPersistent(game, messageLabel);")]
        [InlineData("RewindInvoker.cs", "FlightDriver.StartAndFocusVessel(game, activeIdx);")]
        [InlineData("RevertInterceptor.cs", "HighLogic.CurrentGame = game;")]
        public void EveryParsekTimeTravelLoad_KeepsTheLiveParameters(string file, string consumer)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string text = SourceScanText.StripCSharpComments(
                RewindInvokerSourceFiles.ReadText(Path.Combine(repo, "Source", "Parsek", file)));

            int capture = text.IndexOf("DifficultySettingsCarry.CaptureLive(", StringComparison.Ordinal);
            int load = text.IndexOf("GamePersistence.LoadGame(", capture < 0 ? 0 : capture, StringComparison.Ordinal);
            int apply = text.IndexOf("DifficultySettingsCarry.ApplyTo(game,", StringComparison.Ordinal);
            int use = text.IndexOf(consumer, apply < 0 ? 0 : apply, StringComparison.Ordinal);
            Assert.True(capture >= 0, file + ": no CaptureLive");
            Assert.Equal(1, Count(text, "DifficultySettingsCarry.CaptureLive("));
            Assert.Equal(1, Count(text, "DifficultySettingsCarry.ApplyTo("));
            Assert.True(load > capture, file + ": CaptureLive must come before LoadGame");
            Assert.True(apply > load, file + ": ApplyTo must come after LoadGame");
            Assert.True(use > apply, file + ": ApplyTo must come before " + consumer);
            Assert.Equal(1, Count(text, "GamePersistence.LoadGame("));
        }

        private static int Count(string text, string needle)
        {
            int n = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }
    }
}
