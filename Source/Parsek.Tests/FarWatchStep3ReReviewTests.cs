using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The far watch's step 3 re-review fixes: a switch replayed after the hand-back keeps stock's
    /// CanSwitchVesselsFar refusal, the ways out of the flight stay live while the watch waits in
    /// Aborted for a scene exit (7.6), a switch asked for during Spawning is replayed or dropped
    /// with a log line, the far-start check runs the entry refusals before the admission, and the
    /// far-watch step of the Switch-To gates stops them.
    /// </summary>
    public class FarWatchReplayStockRuleTests
    {
        // catches: the replay calling SetActiveVessel itself after the gates let stock run, so
        // a map Switch To the game's CanSwitchVesselsFar rule forbids switches anyway.
        [Fact]
        public void MapReplay_KeepsStocksFarSwitchRule()
        {
            Assert.False(MapFocusObjectOnSelectPatch.ReplayMaySwitch(true, false));
            Assert.True(MapFocusObjectOnSelectPatch.ReplayMaySwitch(true, true));
            // Stock's alarm Jump and an outside switch never read the rule.
            Assert.True(MapFocusObjectOnSelectPatch.ReplayMaySwitch(false, false));
            Assert.True(MapFocusObjectOnSelectPatch.ReplayMaySwitch(false, true));
        }

        // catches: the replay reading the rule but switching anyway, or never posting stock's
        // own message; and the map path not passing the rule to its replay.
        [Fact]
        public void Replay_RefusesBeforeTheSwitch_WithStocksMessage()
        {
            string map = FarWatchSource.Strip(FarWatchSource.Read("Patches", "MapFocusObjectOnSelectPatch.cs"));
            string replay = FarWatchSource.Body(map, "internal static void ReplayOwnedVesselSwitch(");
            int rule = replay.IndexOf("ReplayMaySwitch(stockFarSwitchRule", StringComparison.Ordinal);
            int sw = replay.IndexOf("FlightGlobals.SetActiveVessel(", StringComparison.Ordinal);
            Assert.True(rule >= 0 && sw > rule, "rule=" + rule + " switch=" + sw);
            string refused = replay.Substring(rule, sw - rule);
            Assert.Contains("PostStockFarSwitchRefusal()", refused);
            Assert.Contains("return;", refused);
            Assert.Contains("\"#autoLOC_465647\"", FarWatchSource.Body(map, "private static void PostStockFarSwitchRefusal("));
            string defer = FarWatchSource.Body(map, "private static bool TryDeferSwitchUntilFarWatchEnds(");
            Assert.Contains("bool stockFarSwitchRule = reFlyPath == ReFlySwitchGate.PathMapSwitchTo;", defer);
            Assert.Contains("ReplayOwnedVesselSwitch(target, reFlyPath, label, stockFarSwitchRule)", defer);
            string drone = FarWatchSource.Strip(FarWatchSource.Read("Patches", "CameraDronePatches.cs"));
            Assert.Contains("stockFarSwitchRule: false", drone);
        }
    }

    public class FarWatchAbortedExitTests
    {
        // catches: the pause menu and the altimeter greyed in Aborted, where the watch waits for
        // a scene exit and the stop keys do nothing, so the player has no way out of the flight.
        [Theory]
        [InlineData(0, false)] // Idle
        [InlineData(1, true)]  // Spawning
        [InlineData(2, true)]  // HandoverOut
        [InlineData(3, true)]  // Following
        [InlineData(4, true)]  // HandoverBack
        [InlineData(5, true)]  // Teardown
        [InlineData(6, false)] // Aborted
        public void FlightExits_AreHeldOnlyWhileAHandBackCanCome(int state, bool held)
        {
            Assert.Equal(held, FarWatchTransitions.HoldsTheFlightExits((FarWatchState)state));
        }

        // catches: either block reading IsRunning (true in Aborted) instead of the exit predicate.
        [Fact]
        public void PauseMenuAndAltimeter_ReadTheExitPredicate()
        {
            string pause = FarWatchSource.Strip(FarWatchSource.Read("Patches", "FarWatchPauseMenuPatch.cs"));
            Assert.DoesNotContain(".IsRunning", pause);
            Assert.Contains("f.FarWatch.HoldsTheFlightExits", pause);
            string alt = FarWatchSource.Strip(FarWatchSource.Read("Patches", "FarWatchAltimeterPatch.cs"));
            Assert.DoesNotContain(".IsRunning", alt);
            Assert.Contains("f.FarWatch.HoldsTheFlightExits", alt);
            string fw = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "FarWatchController.cs"));
            Assert.Contains("internal bool HoldsTheFlightExits => FarWatchTransitions.HoldsTheFlightExits(state);", fw);
        }
    }

    public class FarWatchAbortSwitchTests
    {
        // catches: a Switch To during Spawning lost silently when the abort resolves at once.
        [Theory]
        [InlineData(true, true, 1)]   // Replayed
        [InlineData(false, true, 2)]  // Dropped
        [InlineData(true, false, 0)]  // None
        [InlineData(false, false, 0)] // None
        public void AbortSwitch_Decision(bool resolvedAtOnce, bool pending, int expected)
        {
            Assert.Equal((FarWatchAbortSwitch)expected, FarWatchDecisions.DecideAbortSwitch(resolvedAtOnce, pending));
        }

        // catches: the immediate abort clearing the refs without capturing the pending switch,
        // and the no-hand-back aborts keeping a switch that can never run, unlogged.
        [Fact]
        public void ResolveAbort_ReplaysAtOnce_AndDropsWhenWaiting()
        {
            string fw = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "FarWatchController.cs"));
            string abort = FarWatchSource.Body(fw, "private void ResolveAbort(");
            int capture = abort.IndexOf("CapturePostTeardown();", StringComparison.Ordinal);
            int clear = abort.IndexOf("ClearRefs();", StringComparison.Ordinal);
            int idle = abort.IndexOf("FarWatchTrigger.AbortResolved", StringComparison.Ordinal);
            Assert.True(capture >= 0 && capture < idle && capture < clear, "capture=" + capture + " idle=" + idle);
            Assert.Contains("LogAbortSwitch(FarWatchDecisions.DecideAbortSwitch(true,", abort);
            Assert.Contains("DropPendingSwitchForAbort();", abort.Substring(abort.LastIndexOf("return;", StringComparison.Ordinal)));
            string drop = FarWatchSource.Body(fw, "private void DropPendingSwitchForAbort(");
            Assert.Contains("LogAbortSwitch(FarWatchAbortSwitch.Dropped);", drop);
            Assert.Contains("pendingSwitchTarget = null;", drop);
            Assert.Matches(new Regex(@"FarWatchTrigger\.ShipNeverLoaded, FarWatchEndReason\.ShipNeverLoaded\);\s*DropPendingSwitchForAbort\(\);"), fw);
            Assert.Contains("switch asked for during the far watch target=", FarWatchSource.Body(fw, "private void LogAbortSwitch("));
        }
    }

    public class FarWatchComposeStartTests
    {
        private static FarWatchSafetyVerdict Refused(FarWatchRefusal r)
        {
            return new FarWatchSafetyVerdict { Kind = FarWatchVerdictKind.Refused, Reason = r };
        }

        // catches: an entry refusal ignored (the start then reads only the safety verdict), or
        // the admission run (and logged) when the entry already refused.
        [Fact]
        public void EntryRefusal_Wins_AndTheAdmissionIsNotRun()
        {
            int admitted = 0;
            FarWatchStartCheck c = FarWatchDecisions.ComposeStartCheck("active-is-eva",
                () => { admitted++; return new FarWatchSafetyVerdict { Kind = FarWatchVerdictKind.Allowed }; },
                v => "safety");
            Assert.Equal(0, admitted);
            Assert.Equal("active-is-eva", c.Refusal);
            Assert.False(c.HasVerdict);
            Assert.Equal(FarWatchDecisions.EvaHoverText, c.Hover);
        }

        [Fact]
        public void NoEntryRefusal_TheAdmissionDecides()
        {
            int admitted = 0;
            FarWatchStartCheck c = FarWatchDecisions.ComposeStartCheck(null,
                () => { admitted++; return Refused(FarWatchRefusal.NotInAtmosphere); }, v => "safety");
            Assert.Equal(1, admitted);
            Assert.Equal("unsafe:NOT_IN_ATMOSPHERE", c.Refusal);
            Assert.True(c.HasVerdict);
            Assert.Equal("safety", c.Hover);
            FarWatchStartCheck ok = FarWatchDecisions.ComposeStartCheck(null,
                () => new FarWatchSafetyVerdict { Kind = FarWatchVerdictKind.Allowed }, v => "safety");
            Assert.True(ok.IsAllowed);
            Assert.Null(ok.Hover);
        }

        // catches: EvaluateFarStart dropping the entry refusals (entry = null) or not handing
        // them to the composition.
        [Fact]
        public void EvaluateFarStart_FeedsTheEntryRefusalsToTheComposition()
        {
            string fw = FarWatchSource.Strip(FarWatchSource.Read("Controllers", "FarWatchController.cs"));
            string body = FarWatchSource.Body(fw, "internal FarWatchStartCheck EvaluateFarStart(");
            Assert.Contains("string entry = FarWatchTransitions.EvaluateEntryRefusal(host.BuildFarWatchEntryInputs(recordingIndex, IsRunning, follow));", body);
            Assert.Contains("return FarWatchDecisions.ComposeStartCheck(entry, () => EvaluateAdmission(trigger, useCache),", body);
            Assert.Equal(1, Regex.Matches(body, @"\bentry\s*=").Count);
        }
    }

    [Collection("Sequential")]
    public class FarWatchSwitchGateDriveTests : IDisposable
    {
        private readonly List<string> lines = new List<string>();

        public FarWatchSwitchGateDriveTests()
        {
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
        }

        public void Dispose()
        {
            MapFocusObjectOnSelectPatch.FarWatchRunningForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        // catches: the far-watch step taking the switch but the gates below still running
        // (the `return false` after TryDeferSwitchUntilFarWatchEnds dropped): the Re-Fly gate,
        // the rule read and the pre-switch dialog would then read the drone as the active
        // vessel, and stock would switch.
        [Fact]
        public void FarWatchRunning_StopsTheGatesAndStock()
        {
            MapFocusObjectOnSelectPatch.FarWatchRunningForTesting = () => true;
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            // Unity's == reads an object with no native pointer as null.
            FieldInfo cachedPtr = typeof(UnityEngine.Object).GetField(
                "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cachedPtr);
            cachedPtr.SetValue(vessel, new IntPtr(1));
            Guid armed;
            bool runStock = MapFocusObjectOnSelectPatch.PrepareOwnedVesselSwitch(
                vessel, ReFlySwitchGate.PathMapSwitchTo, "Map Switch-To", out armed);
            Assert.False(runStock);
            Assert.Equal(Guid.Empty, armed);
            Assert.Contains(lines, l => l.Contains("[SwitchIntentPatch]") && l.Contains("during a far watch"));
            Assert.DoesNotContain(lines, l => l.Contains("intent not armed"));
        }
    }
}
