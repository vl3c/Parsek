using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>CrewTransfer kerbal= part=</c>: a player's stock
    /// crew transfer between two crewed parts of the active vessel.
    /// </summary>
    public class TestCommandCrewTransferTests
    {
        private const string Verb = "CrewTransfer";

        private static DispatchState At(TestCommandScene scene) => new DispatchState
        {
            Scene = scene,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static ParsedCommand Cmd(string args)
            => TestCommandParser.ParseLine("id=1 cmd=" + Verb + " " + args, 1);

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.Single(kv => kv.Key == key).Value;

        [Fact]
        public void TheVerbIsImplemented_FlightOnly_DefaultBudget_AndMutating()
        {
            Assert.Equal(TestCommandCrewTransfer.Verb, Verb);
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(Verb));
        }

        [Fact]
        public void Dispatch_ExecutesInFlight_EvenWithALiveRecorder()
        {
            // A live recorder is the case the lanes need: the transfer happens while recording.
            var st = At(TestCommandScene.Flight);
            st.Recording = true;
            st.HasTree = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("kerbal=Valentina%20Kerman part=mk1-3pod"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_DefersOutsideFlight()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("kerbal=Valentina%20Kerman part=mk1-3pod"), At(TestCommandScene.SpaceCenter));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
        }

        [Theory]
        [InlineData(null, "mk1-3pod", "crewtransfer-kerbal-arg-missing")]
        [InlineData("", "mk1-3pod", "crewtransfer-kerbal-arg-missing")]
        [InlineData("  ", "mk1-3pod", "crewtransfer-kerbal-arg-missing")]
        [InlineData("Valentina Kerman", null, "crewtransfer-part-arg-missing")]
        [InlineData("Valentina Kerman", " ", "crewtransfer-part-arg-missing")]
        [InlineData(null, null, "crewtransfer-kerbal-arg-missing")]
        public void ParseArgs_RefusesMissingArgs_KerbalFirst(string kerbal, string part, string expected)
        {
            Assert.Equal(expected, TestCommandCrewTransfer.ParseArgs(kerbal, part, out _, out _));
        }

        [Fact]
        public void ParseArgs_KeepsBothVerbatim()
        {
            Assert.Null(TestCommandCrewTransfer.ParseArgs("Valentina Kerman", "mk1-3pod", out string k, out string p));
            Assert.Equal("Valentina Kerman", k);
            Assert.Equal("mk1-3pod", p);
        }

        [Theory]
        [InlineData(false, false, false, false, "crewtransfer-no-active-vessel")]
        [InlineData(true, false, true, false, "crewtransfer-vessel-not-ready")]
        [InlineData(true, true, false, false, "crewtransfer-kerbal-not-aboard")]
        [InlineData(true, true, true, true, "crewtransfer-transfer-running")]
        [InlineData(true, true, true, false, null)]
        public void DecideGate_InApplierOrder(bool vessel, bool ready, bool aboard, bool running, string expected)
        {
            Assert.Equal(expected, TestCommandCrewTransfer.DecideGate(vessel, ready, aboard, running));
        }

        [Fact]
        public void PickDestination_TheOtherPodOfADockedPair()
        {
            // Two identical pods: stock's valid list leaves out the kerbal's own, so the name
            // names exactly one destination.
            var valid = new List<string> { "dockingPort2", "mk1-3pod" };
            var all = new List<string> { "mk1-3pod", "dockingPort2", "fuelTank" };
            Assert.Null(TestCommandCrewTransfer.PickDestination(valid, all, "mk1-3pod", out int index));
            Assert.Equal(1, index);
        }

        [Fact]
        public void PickDestination_SeveralValid_IsAmbiguous_NeverAFirstPick()
        {
            var valid = new List<string> { "mk1-3pod", "mk1-3pod" };
            Assert.Equal(TestCommandCrewTransfer.PartAmbiguousReason,
                TestCommandCrewTransfer.PickDestination(valid, valid, "mk1-3pod", out int index));
            Assert.Equal(-1, index);
        }

        [Fact]
        public void PickDestination_PresentButNotValid_IsUnavailable()
        {
            // A full pod (or one refusing transfers) is on the vessel but not in stock's list.
            Assert.Equal(TestCommandCrewTransfer.TransferUnavailableReason,
                TestCommandCrewTransfer.PickDestination(new List<string>(),
                    new List<string> { "mk1-3pod" }, "mk1-3pod", out _));
        }

        [Fact]
        public void PickDestination_Absent_IsNotFound_AndMatchesOrdinally()
        {
            Assert.Equal(TestCommandCrewTransfer.PartNotFoundReason,
                TestCommandCrewTransfer.PickDestination(new List<string> { "mk1-3pod" },
                    new List<string> { "mk1-3pod" }, "MK1-3POD", out _));
            Assert.Equal(TestCommandCrewTransfer.PartNotFoundReason,
                TestCommandCrewTransfer.PickDestination(null, null, "mk1-3pod", out _));
        }

        [Theory]
        [InlineData(true, true, 0.1, "Moved")]
        [InlineData(true, false, 0.1, "StillWaiting")]
        [InlineData(false, true, 0.1, "StillWaiting")]
        [InlineData(false, true, 60.0, "Timeout")]
        [InlineData(true, true, 60.0, "Moved")]
        public void DecideCompletion_NeedsTheDismissAndTheSeat(bool dismissed, bool seated, double elapsed,
            string expected)
        {
            Assert.Equal(expected,
                TestCommandCrewTransfer.DecideCompletion(dismissed, seated, elapsed, 60.0).ToString());
        }

        [Fact]
        public void Payload_AndLine_CarryBothPartsAndCounts()
        {
            var payload = TestCommandCrewTransfer.BuildOkPayload(
                "Valentina Kerman", 3620499050u, "mk1-3pod", 11u, "mk1-3pod", 22u, 2, 2);
            Assert.Equal(new[] { "kerbal", "pid", "from", "fromId", "to", "toId", "crewFrom", "crewTo" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal("3620499050", Value(payload, "pid"));
            Assert.Equal("22", Value(payload, "toId"));
            Assert.Equal(
                "crewtransfer applied kerbal='Valentina Kerman' pid=3620499050 from=mk1-3pod fromId=11 "
                + "to=mk1-3pod toId=22 crewFrom=2 crewTo=2",
                TestCommandCrewTransfer.FormatAppliedLine(
                    "Valentina Kerman", 3620499050u, "mk1-3pod", 11u, "mk1-3pod", 22u, 2, 2));
        }

        [Fact]
        public void TheReasonTables_AreDistinctAndPrefixed()
        {
            var all = TestCommandCrewTransfer.Reasons.Concat(TestCommandCrewTransfer.ErrorReasons).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.All(all, r => Assert.StartsWith("crewtransfer-", r));
        }
    }
}
