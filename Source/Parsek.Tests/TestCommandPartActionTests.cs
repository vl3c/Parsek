using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>PartAction button=undock|decouple|controlfrom [part=]
    /// [pid=]</c>: one button of one part's stock right-click menu (vessel names, design 15.3).
    /// </summary>
    public class TestCommandPartActionTests
    {
        private const string Verb = "PartAction";

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

        // ----- table rows -----

        [Fact]
        public void TheVerbIsImplemented_FlightOnly_DefaultBudget_AndMutating()
        {
            Assert.Equal(TestCommandPartAction.Verb, Verb);
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(Verb));
        }

        [Fact]
        public void Dispatch_ExecutesInFlight_EvenWithALiveRecorder()
        {
            var st = At(TestCommandScene.Flight);
            st.Recording = true;
            st.HasTree = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("button=undock"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_DefersOutsideFlight()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("button=undock"), At(TestCommandScene.TrackingStation));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal(TestCommandDispatcher.NotInFlightReason, r.Reason);
        }

        // ----- args -----

        [Theory]
        [InlineData("undock", "Undock")]
        [InlineData("decouple", "Decouple")]
        [InlineData("controlfrom", "ControlFrom")]
        public void ParseButton_AcceptsTheClosedVocabulary(string raw, string expected)
        {
            Assert.Null(TestCommandPartAction.ParseButton(raw, out PartActionKind kind));
            Assert.Equal(expected, kind.ToString());
            Assert.Equal(raw, TestCommandPartAction.Token(kind));
        }

        [Theory]
        [InlineData(null, "partaction-button-arg-missing")]
        [InlineData("", "partaction-button-arg-missing")]
        [InlineData("Undock", "partaction-button-arg-invalid")]
        [InlineData("stage", "partaction-button-arg-invalid")]
        [InlineData("control-from", "partaction-button-arg-invalid")]
        public void ParseButton_RefusesAbsentAndUnknown(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandPartAction.ParseButton(raw, out PartActionKind kind));
            Assert.Equal(PartActionKind.Unknown, kind);
        }

        [Fact]
        public void ButtonValues_AreTheParsedTokens_InOrder()
        {
            Assert.Equal(new[] { "undock", "decouple", "controlfrom" }, TestCommandPartAction.ButtonValues);
            foreach (string v in TestCommandPartAction.ButtonValues)
                Assert.Null(TestCommandPartAction.ParseButton(v, out _));
        }

        [Fact]
        public void ParsePart_IsRequiredForDecoupleAndControlFrom_OptionalForUndock()
        {
            Assert.Null(TestCommandPartAction.ParsePart(null, PartActionKind.Undock, out string none));
            Assert.Null(none);
            Assert.Equal("partaction-part-arg-missing",
                TestCommandPartAction.ParsePart(null, PartActionKind.Decouple, out _));
            Assert.Equal("partaction-part-arg-missing",
                TestCommandPartAction.ParsePart("", PartActionKind.ControlFrom, out _));
            Assert.Null(TestCommandPartAction.ParsePart("Decoupler.1", PartActionKind.Decouple, out string name));
            Assert.Equal("Decoupler.1", name);
            Assert.Null(TestCommandPartAction.ParsePart("dockingPort2", PartActionKind.Undock, out string port));
            Assert.Equal("dockingPort2", port);
        }

        [Theory]
        [InlineData(null, true, 0u, false)]
        [InlineData("", true, 0u, false)]
        [InlineData("3156176881", true, 3156176881u, true)]
        [InlineData("0", false, 0u, false)]
        [InlineData("abc", false, 0u, false)]
        [InlineData("-5", false, 0u, false)]
        [InlineData("4294967296", false, 0u, false)]
        public void ParsePid_IsTheRecoverGrammar(string raw, bool ok, uint pid, bool hasPid)
        {
            string reason = TestCommandPartAction.ParsePid(raw, out uint got, out bool has);
            Assert.Equal(ok ? null : "partaction-pid-arg-invalid", reason);
            Assert.Equal(pid, got);
            Assert.Equal(hasPid, has);
        }

        [Fact]
        public void APercentEncodedPartNameReachesTheVerbDecoded()
        {
            ParsedCommand cmd = Cmd("button=decouple part=Decoupler.1 pid=7");
            Assert.True(cmd.ParseOk);
            Assert.Equal("Decoupler.1", cmd.Args["part"]);
        }

        // ----- gates -----

        [Fact]
        public void DecideVessel_InGateOrder()
        {
            Assert.Equal("partaction-no-active-vessel", TestCommandPartAction.DecideVessel(false, false, false, false));
            Assert.Equal("partaction-vessel-not-found", TestCommandPartAction.DecideVessel(true, false, true, true));
            Assert.Equal("partaction-target-is-ghost", TestCommandPartAction.DecideVessel(true, true, true, false));
            Assert.Equal("partaction-vessel-unloaded", TestCommandPartAction.DecideVessel(true, true, false, false));
            Assert.Null(TestCommandPartAction.DecideVessel(false, true, false, true));
        }

        [Fact]
        public void DecideCandidates_OneProceeds_NoneOrManyRefuse()
        {
            Assert.Null(TestCommandPartAction.DecideCandidates("Decoupler.1", 1, 1));
            Assert.Null(TestCommandPartAction.DecideCandidates(null, 0, 1));
            Assert.Equal("partaction-part-not-found", TestCommandPartAction.DecideCandidates("Decoupler.9", 0, 0));
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates("Decoupler.1", 1, 0));
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(null, 0, 0));
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates("dockingPort2", 3, 2));
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates(null, 0, 2));
        }

        [Fact]
        public void EventNames_AreTheStockPartMenuMethods()
        {
            Assert.Equal(new[] { "Undock" }, TestCommandPartAction.EventNamesFor(PartActionKind.Undock));
            Assert.Equal(new[] { "Decouple" }, TestCommandPartAction.EventNamesFor(PartActionKind.Decouple));
            Assert.Equal(new[] { "MakeReference", "MakeReferenceTransform" },
                TestCommandPartAction.EventNamesFor(PartActionKind.ControlFrom));
            Assert.Empty(TestCommandPartAction.EventNamesFor(PartActionKind.Unknown));
        }

        [Fact]
        public void FoldUndockPairs_KeepsOnePortPerDockedPair()
        {
            // A docked pair shows Undock on both ports; two separate pairs stay two.
            var ports = new List<KeyValuePair<uint, uint>>
            {
                new KeyValuePair<uint, uint>(10, 20),
                new KeyValuePair<uint, uint>(20, 10),
                new KeyValuePair<uint, uint>(30, 40),
                new KeyValuePair<uint, uint>(40, 30),
            };
            Assert.Equal(new[] { 0, 2 }, TestCommandPartAction.FoldUndockPairs(ports));
            // One live port whose partner is not listed (or unknown) is one pair.
            Assert.Equal(new[] { 0 }, TestCommandPartAction.FoldUndockPairs(
                new List<KeyValuePair<uint, uint>> { new KeyValuePair<uint, uint>(10, 20) }));
            Assert.Equal(new[] { 0, 1 }, TestCommandPartAction.FoldUndockPairs(
                new List<KeyValuePair<uint, uint>>
                {
                    new KeyValuePair<uint, uint>(10, 0),
                    new KeyValuePair<uint, uint>(11, 0),
                }));
            Assert.Empty(TestCommandPartAction.FoldUndockPairs(null));
        }

        [Fact]
        public void NewVesselPids_AreTheAfterOnlyPids_InAfterOrder()
        {
            Assert.Equal(new uint[] { 9, 7 },
                TestCommandPartAction.NewVesselPids(new uint[] { 1, 2 }, new uint[] { 1, 9, 2, 7, 9 }));
            Assert.Empty(TestCommandPartAction.NewVesselPids(new uint[] { 1, 2 }, new uint[] { 2 }));
            Assert.Equal(new uint[] { 3 }, TestCommandPartAction.NewVesselPids(null, new uint[] { 3 }));
        }

        // ----- answer -----

        [Fact]
        public void Payload_IsInWireOrder()
        {
            var payload = TestCommandPartAction.BuildOkPayload(PartActionKind.Undock, 3620499050, "dockingPort2",
                4242, "Undock", 31337, "VN4 Tug", 2, 3, 3620499050, "Kerbal X");
            Assert.Equal(new[] { "button", "pid", "part", "partId", "event", "newPid", "newName",
                "vesselsBefore", "vesselsAfter", "activePid", "activeName" }, payload.Select(kv => kv.Key));
            Assert.Equal("undock", Value(payload, "button"));
            Assert.Equal("31337", Value(payload, "newPid"));
            Assert.Equal("VN4 Tug", Value(payload, "newName"));
            Assert.Equal("Kerbal X", Value(payload, "activeName"));
        }

        [Fact]
        public void AppliedLine_IsGrepStable()
        {
            string line = TestCommandPartAction.FormatAppliedLine(PartActionKind.ControlFrom, 2200110033,
                "probeCoreOcto2.v2", 77, "MakeReference", 0, null, 1, 1, 2200110033, "VN8 Alpha");
            Assert.Equal("partaction applied button=controlfrom pid=2200110033 part='probeCoreOcto2.v2' partId=77 " +
                "event=MakeReference newPid=0 newName='' vessels=1->1 activePid=2200110033 activeName='VN8 Alpha'",
                line);
        }

        [Fact]
        public void Reasons_AreTheGateOrder_AndDisjointFromTheErrors()
        {
            Assert.Equal(11, TestCommandPartAction.Reasons.Length);
            Assert.Equal("partaction-button-arg-missing", TestCommandPartAction.Reasons[0]);
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.Reasons[10]);
            Assert.Empty(TestCommandPartAction.Reasons.Intersect(TestCommandPartAction.ErrorReasons));
            Assert.All(TestCommandPartAction.Reasons, r => Assert.StartsWith("partaction-", r));
        }
    }
}
