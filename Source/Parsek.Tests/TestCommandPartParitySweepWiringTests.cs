using System.Collections.Generic;
using System.IO;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The <c>PartParitySweep</c> verb's seam wiring (design-part-ghost-parity.md section 7):
    /// the verb tables, the dispatch scene gate, the 540 s budget, the pure completion half
    /// (<see cref="PartParitySweepCompletion"/>) and the applier's order (parse, then admit,
    /// then start the runner).
    /// </summary>
    public class TestCommandPartParitySweepWiringTests
    {
        private static ParsedCommand Cmd()
            => TestCommandParser.ParseLine("id=1 cmd=PartParitySweep label=pp1 op=run", 1);

        private static DispatchState At(TestCommandScene scene)
            => new DispatchState { Scene = scene, GameLoaded = true, SettingsPresent = true };

        [Fact]
        public void Verb_IsImplementedFlightOnlyAndMutating()
        {
            string verb = TestCommandPartParitySweep.VerbName;
            Assert.Equal("PartParitySweep", verb);
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(verb));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor(verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(verb));
            Assert.False(TestCommandDispatcher.RejectsOutsideFlight(verb));
        }

        [Fact]
        public void Dispatch_ExecutesInFlight_DefersElsewhere()
        {
            Assert.Equal(DispatchDecision.Execute,
                TestCommandDispatcher.DecideDispatch(Cmd(), At(TestCommandScene.Flight)).Decision);
            foreach (TestCommandScene scene in new[]
                     { TestCommandScene.SpaceCenter, TestCommandScene.TrackingStation, TestCommandScene.MainMenu })
            {
                DispatchResult r = TestCommandDispatcher.DecideDispatch(Cmd(), At(scene));
                Assert.Equal(DispatchDecision.Defer, r.Decision);
                Assert.Equal(TestCommandDispatcher.NotInFlightReason, r.Reason);
            }
        }

        [Fact]
        public void Budget_Is540_AndCoversTheLargestTimeBox()
        {
            double budget = DeferralBudget.BudgetSeconds(TestCommandPartParitySweep.VerbName);
            Assert.Equal(540.0, budget);
            Assert.Equal(DeferralBudget.PartParitySweepSeconds, budget);
            // The step time-boxes itself; the budget must leave the last wave room to tear down.
            Assert.True(budget >= TestCommandPartParitySweep.MaxMaxSeconds + 60.0);
        }

        // ----- poll -----

        [Theory]
        [InlineData(false, false, false, false, "NotYet")]
        [InlineData(true, true, false, false, "Done")]
        [InlineData(true, false, false, false, "Failed")]
        [InlineData(false, false, true, false, "Failed")]
        [InlineData(false, false, false, true, "TimedOut")]
        // A result on the frame the budget expired still answers it.
        [InlineData(true, true, true, true, "Done")]
        [InlineData(true, false, false, true, "Failed")]
        // A coroutine that ended without a report fails at once, even past the budget.
        [InlineData(false, false, true, true, "Failed")]
        public void DecidePoll_Table(bool arrived, bool ok, bool ended, bool expired, string expected)
        {
            Assert.Equal(expected,
                PartParitySweepCompletion.DecidePoll(arrived, ok, ended, expired).ToString());
        }

        [Fact]
        public void ErrorReasons_AreTheTwoTerminals_DisjointFromTheRefusals()
        {
            Assert.Equal(new[] { "partparity-step-failed", "partparity-timeout" },
                PartParitySweepCompletion.ErrorReasons);
            foreach (string r in PartParitySweepCompletion.ErrorReasons)
                Assert.DoesNotContain(r, TestCommandPartParitySweep.Reasons);
        }

        [Fact]
        public void FormatStepFailedMessage_CarriesTheRunnerReason()
        {
            Assert.Equal("partparity-step-failed reason=not-implemented",
                PartParitySweepCompletion.FormatStepFailedMessage("not-implemented", null));
            Assert.Equal("partparity-step-failed reason=no-result",
                PartParitySweepCompletion.FormatStepFailedMessage(null, null));
            Assert.Equal("partparity-step-failed reason=spawn_failed detail=wave_3_of_7",
                PartParitySweepCompletion.FormatStepFailedMessage("spawn failed", "wave 3 of 7"));
        }

        [Fact]
        public void OkPayload_IsTheRunnersPayload()
        {
            var payload = TestCommandPartParitySweep.BuildPayload(
                "pp1", true, 20, 20, 19, 0, 1, TestCommandPartParitySweep.ControlsOk, 123.47);
            Assert.Same(payload, PartParitySweepCompletion.OkPayload(payload));
            Assert.Empty(PartParitySweepCompletion.OkPayload(null));
            Assert.Equal(
                "partparitysweep complete label=pp1 elapsed=123.4s label=pp1 done=true next=20 cases=20 "
                + "complete=19 crashed=0 setupFaults=1 controls=ok wall=123.5",
                PartParitySweepCompletion.FormatOkLine(
                    "pp1", TestCommandPartParitySweep.FormatPayload(payload), 123.44));
        }

        // ----- the applier's order -----

        private static string ApplierSource()
        {
            string root = Path.GetFullPath(Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "TestCommands",
                "ParsekTestCommandAddon.PartParitySweep.cs");
            Assert.True(File.Exists(path), path);
            return File.ReadAllText(path);
        }

        [Fact]
        public void Applier_ParsesThenAdmitsThenStartsTheRunner()
        {
            string text = ApplierSource();
            int parse = text.IndexOf("TestCommandPartParitySweep.TryParseArgs(", System.StringComparison.Ordinal);
            int admit = text.IndexOf("TestCommandPartParitySweep.DecideHostAdmission(", System.StringComparison.Ordinal);
            int start = text.IndexOf("StartCoroutine(PartParitySweepRoutine(", System.StringComparison.Ordinal);
            int run = text.IndexOf("PartParitySweepRunner.RunStep(", System.StringComparison.Ordinal);
            Assert.True(parse >= 0 && admit > parse && start > admit && run > start,
                $"parse={parse} admit={admit} start={start} run={run}");
            Assert.Contains("SetExecResult(PendingVerdict, null, null);", text);
            Assert.Contains("PartParitySweepCompletion.DecidePoll(", text);
        }
    }
}
