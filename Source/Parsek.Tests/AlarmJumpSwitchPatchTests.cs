using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-13: the stock alarm dialog's Jump button switches vessels outside every other
    /// Parsek switch patch. These cells pin the decision, the Re-Fly refusal path and the
    /// patch targets.
    /// </summary>
    [Collection("Sequential")]
    public class AlarmJumpSwitchPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AlarmJumpSwitchPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ReFlySwitchGate.ResetForTesting();
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            ReFlySwitchGate.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private void InstallSession()
        {
            var tree = new RecordingTree { Id = "tree-1" };
            tree.Recordings["P"] = new Recording { RecordingId = "P", VesselPersistentId = 100u, TreeId = "tree-1" };
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "P",
            };
            ReFlySwitchGate.LiveSessionProviderForTesting = () =>
                new ReFlySwitchGate.LiveSession { Marker = marker, Tree = tree };
        }

        [Theory]
        [InlineData(false, false, "LeaveToStock")]
        [InlineData(true, false, "RefuseGhost")]
        [InlineData(true, true, "RefuseGhost")]
        [InlineData(false, true, "RefuseReFly")]
        public void Decide_Matrix(bool ghost, bool reFlyRefused, string expected)
        {
            Assert.Equal(expected, AlarmJumpSwitchPatch.Decide(ghost, reFlyRefused).ToString());
        }

        [Fact]
        public void DecideLive_GhostMapVessel_IsRefusedWithoutAReFlySession()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(555u);

            Assert.Equal(AlarmJumpSwitchPatch.JumpDecision.RefuseGhost,
                AlarmJumpSwitchPatch.DecideLive(555u, log: true));
            Assert.Equal(AlarmJumpSwitchPatch.JumpDecision.LeaveToStock,
                AlarmJumpSwitchPatch.DecideLive(556u, log: true));
        }

        [Fact]
        public void DecideLive_ReFlySession_RefusesAnotherVesselOnTheAlarmPath()
        {
            InstallSession();

            Assert.Equal(AlarmJumpSwitchPatch.JumpDecision.RefuseReFly,
                AlarmJumpSwitchPatch.DecideLive(200u, log: true));
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Switch refused sess=sess-1 path=alarmJump target=200 reason=notSessionVessel"));

            Assert.Equal(AlarmJumpSwitchPatch.JumpDecision.LeaveToStock,
                AlarmJumpSwitchPatch.DecideLive(100u, log: true));
        }

        [Fact]
        public void DecideLive_SilentForTheButtonMark()
        {
            InstallSession();

            Assert.Equal(AlarmJumpSwitchPatch.JumpDecision.RefuseReFly,
                AlarmJumpSwitchPatch.DecideLive(200u, log: false));
            Assert.DoesNotContain(logLines, l => l.Contains("Switch refused"));
        }

        [Theory]
        [InlineData(GameScenes.TRACKSTATION, "TrackingStationFly", "TrackingStation")]
        [InlineData(GameScenes.SPACECENTER, "KscMarkerFly", "SpaceCenter")]
        public void TryMapSceneToIntent_MirrorsTheScenesFlyControl(
            GameScenes scene, string action, string source)
        {
            Assert.True(AlarmJumpSwitchPatch.TryMapSceneToIntent(scene, out var a, out var s));
            Assert.Equal(action, a.ToString());
            Assert.Equal(source, s.ToString());
        }

        [Theory]
        [InlineData(GameScenes.EDITOR)]
        [InlineData(GameScenes.MAINMENU)]
        public void TryMapSceneToIntent_NoFlyControl_ArmsNothing(GameScenes scene)
        {
            Assert.False(AlarmJumpSwitchPatch.TryMapSceneToIntent(scene, out _, out _));
        }

        [Fact]
        public void Patches_TargetStockAlarmDialogMembers()
        {
            MethodInfo onJump = typeof(AlarmClockMessageDialog).GetMethod(
                "OnJump", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(onJump);
            MethodInfo spawn = typeof(AlarmClockMessageDialog).GetMethod(
                "Spawn", BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(spawn);
            Assert.Equal(typeof(AlarmClockMessageDialog), spawn.ReturnType);

            Assert.NotNull(AccessTools.Field(typeof(AlarmClockMessageDialog), "alarm"));
            Assert.NotNull(AccessTools.Field(typeof(AlarmClockMessageDialog), "jumpButton"));
            Assert.NotNull(AccessTools.Field(typeof(AlarmTypeBase), "vesselId"));

            MethodInfo prefix = typeof(AlarmJumpSwitchPatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.Equal(typeof(bool), prefix.ReturnType);

            Assert.NotEmpty(typeof(AlarmJumpSwitchPatch).GetCustomAttributes(typeof(HarmonyPatch), false));
            Assert.NotEmpty(typeof(AlarmJumpButtonMarkPatch).GetCustomAttributes(typeof(HarmonyPatch), false));
        }
    }
}
