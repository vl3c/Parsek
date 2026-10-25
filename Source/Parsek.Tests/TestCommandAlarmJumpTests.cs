using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the <c>AlarmJump</c> seam verb (GC-13: the stock alarm dialog's Jump
    /// bypassed every Parsek switch rule), the stock members its applier drives, and its
    /// dispatch guards.
    /// </summary>
    public class TestCommandAlarmJumpTests
    {
        [Theory]
        [InlineData(null, null, null, "alarmjump-target-arg-missing")]
        [InlineData("", "", null, "alarmjump-target-arg-missing")]
        [InlineData("WP Orbital Station", "12", null, "alarmjump-target-arg-conflict")]
        [InlineData(null, "abc", null, "alarmjump-pid-arg-invalid")]
        [InlineData(null, "0", null, "alarmjump-pid-arg-invalid")]
        [InlineData(null, "-5", null, "alarmjump-pid-arg-invalid")]
        [InlineData(null, "4294967296", null, "alarmjump-pid-arg-invalid")]
        [InlineData("WP Orbital Station", null, "Always", "alarmjump-jump-arg-invalid")]
        [InlineData("WP Orbital Station", null, "", "alarmjump-jump-arg-invalid")]
        public void ParseArgs_Refusals(string vessel, string pid, string press, string expected)
        {
            Assert.Equal(expected, TestCommandAlarmJump.ParseArgs(vessel, pid, press, out _, out _, out _));
            Assert.Contains(expected, TestCommandAlarmJump.Reasons);
        }

        [Theory]
        [InlineData("WP Orbital Station", null, null, "WP Orbital Station", 0u, "shown")]
        [InlineData(null, "4242", "always", null, 4242u, "always")]
        [InlineData(null, "4242", "never", null, 4242u, "never")]
        [InlineData("Kerbal X", null, "shown", "Kerbal X", 0u, "shown")]
        public void ParseArgs_Accepts(string vessel, string pid, string press,
            string wantName, uint wantPid, string wantPress)
        {
            Assert.Null(TestCommandAlarmJump.ParseArgs(vessel, pid, press,
                out string name, out uint p, out string pr));
            Assert.Equal(wantName, name);
            Assert.Equal(wantPid, p);
            Assert.Equal(wantPress, pr);
        }

        [Fact]
        public void Reasons_AreInGateOrder_AndDisjointFromErrors()
        {
            Assert.Equal(new[]
            {
                TestCommandAlarmJump.TargetArgMissingReason,
                TestCommandAlarmJump.TargetArgConflictReason,
                TestCommandAlarmJump.PidArgInvalidReason,
                TestCommandAlarmJump.JumpArgInvalidReason,
                TestCommandAlarmJump.NoAlarmClockReason,
                TestCommandAlarmJump.TargetNotFoundReason,
                TestCommandAlarmJump.TargetAmbiguousReason,
            }, TestCommandAlarmJump.Reasons);
            Assert.Equal(new[]
            {
                TestCommandAlarmJump.AddFailedReason,
                TestCommandAlarmJump.TriggerFailedReason,
                TestCommandAlarmJump.NoDialogReason,
                TestCommandAlarmJump.JumpThrewReason,
            }, TestCommandAlarmJump.ErrorReasons);
            Assert.Empty(TestCommandAlarmJump.Reasons.Intersect(TestCommandAlarmJump.ErrorReasons));
        }

        [Theory]
        [InlineData(0, "alarmjump-target-not-found")]
        [InlineData(1, null)]
        [InlineData(2, "alarmjump-target-ambiguous")]
        public void ClassifyMatches(int matches, string expected)
        {
            Assert.Equal(expected, TestCommandAlarmJump.ClassifyMatches(matches));
        }

        [Theory]
        // Stock's Spawn: a vessel id, live, and not the active vessel (no active vessel = other).
        [InlineData(0u, true, 1u, false)]
        [InlineData(5u, false, 1u, false)]
        [InlineData(5u, true, 5u, false)]
        [InlineData(5u, true, 1u, true)]
        [InlineData(5u, true, 0u, true)]
        public void StockWouldShowJump_MirrorsStockSpawn(uint vesselId, bool live, uint active, bool expected)
        {
            Assert.Equal(expected, TestCommandAlarmJump.StockWouldShowJump(vesselId, live, active));
        }

        [Theory]
        [InlineData("shown", true, true)]
        [InlineData("shown", false, false)]
        [InlineData("always", false, true)]
        [InlineData("always", true, true)]
        [InlineData("never", true, false)]
        public void ShouldPress(string press, bool shown, bool expected)
        {
            Assert.Equal(expected, TestCommandAlarmJump.ShouldPress(press, shown));
        }

        [Fact]
        public void DecisionTokens_OnePerPatchDecision_ThenInert()
        {
            var members = (AlarmJumpSwitchPatch.JumpDecision[])Enum.GetValues(typeof(AlarmJumpSwitchPatch.JumpDecision));
            Assert.Equal(members.Length + 1, TestCommandAlarmJump.Decisions.Length);
            for (int i = 0; i < members.Length; i++)
                Assert.Equal(TestCommandAlarmJump.Decisions[i], TestCommandAlarmJump.DecisionToken(members[i]));
            Assert.Equal("refuse-refly", TestCommandAlarmJump.DecisionToken(AlarmJumpSwitchPatch.JumpDecision.RefuseReFly));
            Assert.Equal("refuse-ghost", TestCommandAlarmJump.DecisionToken(AlarmJumpSwitchPatch.JumpDecision.RefuseGhost));
            Assert.Equal("inert", TestCommandAlarmJump.Decisions.Last());
            Assert.Equal("unknown", TestCommandAlarmJump.DecisionToken((AlarmJumpSwitchPatch.JumpDecision)99));
        }

        [Fact]
        public void Payload_WireOrder_AndResultLine()
        {
            var payload = TestCommandAlarmJump.BuildOkPayload(4242u, false, false, 77u,
                true, false, "refuse-refly", "always", true, 100u, 100u, false, true);
            Assert.Equal(new[]
            {
                "targetPid", "targetIsGhost", "targetLoaded", "alarmId", "stockWouldShowJump",
                "jumpShown", "decision", "jump", "pressed", "before", "after", "switched",
                "sceneLoadRequested", "alarmRemoved",
            }, payload.Select(kv => kv.Key).ToArray());
            Assert.Equal(
                "alarmjump result targetPid=4242 targetIsGhost=false targetLoaded=false alarmId=77 " +
                "stockWouldShowJump=true jumpShown=false decision=refuse-refly jump=always pressed=true " +
                "before=100 after=100 switched=false sceneLoadRequested=false alarmRemoved=true",
                TestCommandAlarmJump.FormatResultLine(payload));
        }

        [Fact]
        public void Payload_Switched_WhenTheActiveVesselMoved()
        {
            var payload = TestCommandAlarmJump.BuildOkPayload(4242u, false, true, 1u,
                true, true, "leave-to-stock", "shown", true, 100u, 4242u, false, true);
            Assert.Equal("true", payload.Single(kv => kv.Key == "switched").Value);
            var none = TestCommandAlarmJump.BuildOkPayload(4242u, false, true, 1u,
                true, true, null, null, false, 0u, 0u, false, true);
            Assert.Equal("none", none.Single(kv => kv.Key == "before").Value);
            Assert.Equal("unknown", none.Single(kv => kv.Key == "decision").Value);
            Assert.Equal("shown", none.Single(kv => kv.Key == "jump").Value);
        }

        [Fact]
        public void StockMembers_TheApplierDrives_Exist()
        {
            // Every member the applier reaches by name must exist in KSP 1.12.5, or the verb
            // answers ERROR in flight instead of driving stock.
            Assert.NotNull(AccessTools.Method(typeof(AlarmTypeBase), "TriggerAlarm"));
            Assert.NotNull(AccessTools.Field(typeof(AlarmTypeBase), "dialogObject"));
            Assert.Equal(typeof(AlarmClockMessageDialog),
                AccessTools.Field(typeof(AlarmTypeBase), "dialogObject").FieldType);
            Assert.NotNull(AccessTools.Method(typeof(AlarmClockMessageDialog), "OnJump"));
            Assert.NotNull(AccessTools.Method(typeof(AlarmClockMessageDialog), "OnDeleteOnClose"));
            Assert.NotNull(AccessTools.Field(typeof(AlarmClockMessageDialog), "jumpButton"));
            Assert.True(typeof(AlarmTypeBase).IsAssignableFrom(typeof(AlarmTypeRaw)));
            Assert.NotNull(typeof(AlarmClockScenario).GetMethod("AddAlarm",
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(AlarmTypeBase) }, null));
            Assert.NotNull(typeof(AlarmClockScenario).GetMethod("DeleteAlarm",
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(uint) }, null));
            Assert.NotNull(typeof(AlarmActions).GetConstructor(new[]
            {
                typeof(AlarmActions.WarpEnum), typeof(AlarmActions.MessageEnum), typeof(bool), typeof(bool),
            }));
        }

        [Fact]
        public void TheApplier_FiresAndPressesThroughStock()
        {
            string src = Source("TestCommands", "ParsekTestCommandAddon.AlarmJump.cs");
            int add = src.IndexOf("AlarmClockScenario.AddAlarm(alarm)", StringComparison.Ordinal);
            int trigger = src.IndexOf("AccessTools.Method(typeof(AlarmTypeBase), \"TriggerAlarm\")", StringComparison.Ordinal);
            int read = src.IndexOf("Field(\"jumpButton\")", StringComparison.Ordinal);
            int press = src.IndexOf("AccessTools.Method(typeof(AlarmClockMessageDialog), \"OnJump\")", StringComparison.Ordinal);
            int close = src.IndexOf("CloseAlarmJumpDialog(dialog, alarm.Id);", press, StringComparison.Ordinal);
            Assert.True(add >= 0 && trigger > add && read > trigger && press > read && close > press, src);
            Assert.Contains("\"OnDeleteOnClose\"", src);
            // The verb never calls SetActiveVessel itself: the switch is stock's, behind the Prefix.
            Assert.DoesNotContain("SetActiveVessel", src);
        }

        private static string Source(params string[] parts)
        {
            var all = new List<string> { AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Source", "Parsek" };
            all.AddRange(parts);
            return File.ReadAllText(Path.Combine(all.ToArray()));
        }

        // ----- Dispatch -----

        private static ParsedCommand Cmd(string line) => TestCommandParser.ParseLine(line, 1);

        private static DispatchState Flight() => new DispatchState
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            SettingsPresent = true,
        };

        [Fact]
        public void Dispatch_OutsideFlight_Defers()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AlarmJump pid=5"),
                new DispatchState { Scene = TestCommandScene.MainMenu });
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_LoadInFlight_Rejects()
        {
            var st = Flight();
            st.LoadInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AlarmJump pid=5"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_MergeJournalInFlight_Rejects()
        {
            var st = Flight();
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AlarmJump pid=5"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_SettledFlight_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AlarmJump pid=5"), Flight());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }
    }
}
