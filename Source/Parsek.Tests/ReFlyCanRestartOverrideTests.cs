using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// S7 (owner ruling 2026-09-26): on the Hard preset (Flight.CanRestart = false) stock
    /// hides the Revert Flight / Revert to Launch buttons, so the re-fly Retry dialog is
    /// unreachable. <see cref="ReFlyRevertButtonGate"/> sets the live FlightParams'
    /// CanRestart to true in memory only while a re-fly is live in FLIGHT, restores it on
    /// every exit, and the save-time guard rewrites any serialization of the held instance
    /// back to False. These cells drive the pure decision and the core over real
    /// <c>GameParameters.FlightParams</c> objects.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyCanRestartOverrideTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReFlyCanRestartOverrideTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ReFlyRevertButtonGate.ResetForTesting();
        }

        public void Dispose()
        {
            ReFlyRevertButtonGate.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static GameParameters.FlightParams Hard()
        {
            return new GameParameters.FlightParams { CanRestart = false };
        }

        [Fact]
        public void Decision_HoldsOnlyForLiveReFlyInFlightOnAFalsePresetWithTheGuard()
        {
            for (int bits = 0; bits < 32; bits++)
            {
                bool reFly = (bits & 1) != 0;
                bool inFlight = (bits & 2) != 0;
                bool leaving = (bits & 4) != 0;
                bool playerCanRestart = (bits & 8) != 0;
                bool guard = (bits & 16) != 0;
                bool expected = reFly && inFlight && !leaving && !playerCanRestart && guard;
                Assert.Equal(expected, ReFlyRevertButtonGate.ShouldHoldCanRestartOverride(
                    reFly, inFlight, leaving, playerCanRestart, guard));
            }
        }

        [Fact]
        public void Force_ThenSave_WritesFalse_AndOtherInstancesAreUntouched()
        {
            var fp = Hard();
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, true, "onFlightReady", "sess_s7");
            Assert.True(fp.CanRestart);
            Assert.True(ReFlyRevertButtonGate.CanRestartOverrideHeldForTesting);
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("forced Parameters.Flight.CanRestart=True in memory at onFlightReady sess=sess_s7"));

            // What stock's ParameterNode.Save writes for the live value.
            var node = new ConfigNode("FLIGHT");
            node.AddValue("CanQuickSave", "False");
            node.AddValue("CanRestart", fp.CanRestart.ToString());
            Assert.True(ReFlyRevertButtonGate.RewritePersistedCanRestart(fp, node));
            Assert.Equal("False", node.GetValue("CanRestart"));
            Assert.Equal("False", node.GetValue("CanQuickSave"));
            Assert.Single(node.GetValues("CanRestart"));
            Assert.Contains(logLines, l => l.Contains("save wrote Parameters.Flight.CanRestart=False"));

            // Any other ParameterNode (another game's FlightParams, a mod's custom node) is untouched.
            var other = new GameParameters.FlightParams { CanRestart = true };
            var otherNode = new ConfigNode("FLIGHT");
            otherNode.AddValue("CanRestart", "True");
            Assert.False(ReFlyRevertButtonGate.RewritePersistedCanRestart(other, otherNode));
            Assert.Equal("True", otherNode.GetValue("CanRestart"));
        }

        [Fact]
        public void NoOverride_SaveGuardIsANoOp()
        {
            var fp = Hard();
            var node = new ConfigNode("FLIGHT");
            node.AddValue("CanRestart", "False");
            Assert.False(ReFlyRevertButtonGate.RewritePersistedCanRestart(fp, node));
            Assert.DoesNotContain(logLines, l => l.Contains("save wrote"));
        }

        [Theory]
        [InlineData("re-fly-ended")]
        [InlineData("not-in-flight")]
        [InlineData("leaving-scene")]
        public void EveryExitPath_RestoresFalse_AndDropsTheHold(string exit)
        {
            var fp = Hard();
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, true, "force", "s");
            Assert.True(fp.CanRestart);

            bool reFly = exit != "re-fly-ended";
            bool inFlight = exit != "not-in-flight";
            bool leaving = exit == "leaving-scene";
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, reFly, inFlight, leaving, true, "exit", null);

            Assert.False(fp.CanRestart);
            Assert.False(ReFlyRevertButtonGate.CanRestartOverrideHeldForTesting);
            Assert.Contains(logLines, l => l.Contains("restored Parameters.Flight.CanRestart=False at exit reason=" + exit));
            // After restore, a save of that instance is left as stock wrote it.
            var node = new ConfigNode("FLIGHT");
            node.AddValue("CanRestart", fp.CanRestart.ToString());
            Assert.False(ReFlyRevertButtonGate.RewritePersistedCanRestart(fp, node));
        }

        [Fact]
        public void GameObjectReplaced_RestoresTheOrphanedInstance_ThenForcesTheNewOne()
        {
            var oldFp = Hard();
            ReFlyRevertButtonGate.ApplyCanRestartOverride(oldFp, true, true, false, true, "force", "s1");
            var newFp = Hard(); // Retry reloads the RP quicksave: a new Game, new Parameters.
            ReFlyRevertButtonGate.ApplyCanRestartOverride(newFp, true, true, false, true, "onFlightReady", "s2");

            Assert.False(oldFp.CanRestart);
            Assert.True(newFp.CanRestart);
            Assert.Contains(logLines, l => l.Contains("reason=game-parameters-replaced"));
            var node = new ConfigNode("FLIGHT");
            node.AddValue("CanRestart", "True");
            Assert.False(ReFlyRevertButtonGate.RewritePersistedCanRestart(oldFp, node));
            Assert.True(ReFlyRevertButtonGate.RewritePersistedCanRestart(newFp, node));
        }

        [Fact]
        public void ReleaseWithNoCurrentGame_StillRestoresTheHeldInstance()
        {
            var fp = Hard();
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, true, "force", "s");
            // xUnit has no HighLogic.CurrentGame, like a main-menu unload.
            ReFlyRevertButtonGate.ReleaseCanRestartOverride("scene-load-requested:MAINMENU");
            Assert.False(fp.CanRestart);
            Assert.False(ReFlyRevertButtonGate.CanRestartOverrideHeldForTesting);
        }

        [Fact]
        public void NormalPreset_IsNeverTouched()
        {
            var fp = new GameParameters.FlightParams { CanRestart = true };
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, true, "onFlightReady", "s");
            Assert.True(fp.CanRestart);
            Assert.False(ReFlyRevertButtonGate.CanRestartOverrideHeldForTesting);
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, false, true, false, true, "cleared", null);
            Assert.True(fp.CanRestart); // never restored to False: it was never ours
            Assert.DoesNotContain(logLines, l => l.Contains("Parameters.Flight.CanRestart"));
        }

        [Fact]
        public void GuardMissing_DoesNotForce_AndWarns()
        {
            var fp = Hard();
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, false, "onFlightReady", "s");
            Assert.False(fp.CanRestart);
            Assert.False(ReFlyRevertButtonGate.CanRestartOverrideHeldForTesting);
            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("persistence guard is not installed"));
        }

        [Fact]
        public void RepeatedApply_IsIdempotent()
        {
            var fp = Hard();
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, true, "a", "s");
            ReFlyRevertButtonGate.ApplyCanRestartOverride(fp, true, true, false, true, "b", "s");
            Assert.True(fp.CanRestart);
            Assert.Single(logLines.Where(l => l.Contains("forced Parameters.Flight.CanRestart")));
            Assert.DoesNotContain(logLines, l => l.Contains("restored Parameters.Flight.CanRestart"));
        }

        [Fact]
        public void PersistPatch_TargetsTheBaseParameterNodeSave()
        {
            var attr = (HarmonyLib.HarmonyPatch)typeof(Parsek.Patches.FlightParamsCanRestartPersistPatch)
                .GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Single();
            Assert.Equal(typeof(GameParameters.ParameterNode), attr.info.declaringType);
            Assert.Equal("Save", attr.info.methodName);
            // FlightParams must not declare its own Save, or the postfix would miss it.
            Assert.Null(typeof(GameParameters.FlightParams).GetMethod("Save",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly, null, new[] { typeof(ConfigNode) }, null));
        }
    }
}
