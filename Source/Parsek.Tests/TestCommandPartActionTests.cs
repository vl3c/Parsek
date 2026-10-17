using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>PartAction button=undock|decouple|controlfrom|deploy|
    /// retract|startconverter|stopconverter|experiment|labprocess|labtransmit|inspect [part=]
    /// [converter=] [pid=]</c>: one button of one part's stock right-click menu (vessel names,
    /// design 15.3; the ISRU / lab buttons).
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
        [InlineData("deploy", "Deploy")]
        [InlineData("retract", "Retract")]
        [InlineData("startconverter", "StartConverter")]
        [InlineData("stopconverter", "StopConverter")]
        [InlineData("experiment", "Experiment")]
        [InlineData("labprocess", "LabProcess")]
        [InlineData("labtransmit", "LabTransmit")]
        [InlineData("inspect", "Inspect")]
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
        [InlineData("StartConverter", "partaction-button-arg-invalid")]
        [InlineData("start-converter", "partaction-button-arg-invalid")]
        [InlineData("process", "partaction-button-arg-invalid")]
        public void ParseButton_RefusesAbsentAndUnknown(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandPartAction.ParseButton(raw, out PartActionKind kind));
            Assert.Equal(PartActionKind.Unknown, kind);
        }

        [Fact]
        public void ButtonValues_AreTheParsedTokens_InOrder()
        {
            Assert.Equal(new[] { "undock", "decouple", "controlfrom", "deploy", "retract", "startconverter",
                "stopconverter", "experiment", "labprocess", "labtransmit", "inspect" },
                TestCommandPartAction.ButtonValues);
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
        [InlineData("decouple", true)]
        [InlineData("controlfrom", true)]
        [InlineData("deploy", true)]
        [InlineData("retract", true)]
        [InlineData("startconverter", true)]
        [InlineData("stopconverter", true)]
        [InlineData("experiment", true)]
        [InlineData("labprocess", true)]
        [InlineData("labtransmit", true)]
        [InlineData("inspect", true)]
        [InlineData("undock", false)]
        public void RequiresPart_EveryButtonButUndock(string token, bool required)
        {
            Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
            Assert.Equal(required, TestCommandPartAction.RequiresPart(kind));
            Assert.Equal(required ? "partaction-part-arg-missing" : null,
                TestCommandPartAction.ParsePart(null, kind, out _));
        }

        [Fact]
        public void RequiresPart_FalseForUnknown()
        {
            Assert.False(TestCommandPartAction.RequiresPart(PartActionKind.Unknown));
        }

        [Theory]
        [InlineData("startconverter", "Lf+Ox", null, "Lf+Ox")]
        [InlineData("stopconverter", "Surface Harvester", null, "Surface Harvester")]
        [InlineData("startconverter", null, null, null)]
        [InlineData("startconverter", "", null, null)]
        [InlineData("deploy", null, null, null)]
        [InlineData("inspect", "", null, null)]
        [InlineData("deploy", "Lf+Ox", "partaction-converter-arg-unexpected", null)]
        [InlineData("inspect", "Lf+Ox", "partaction-converter-arg-unexpected", null)]
        [InlineData("labtransmit", "Research", "partaction-converter-arg-unexpected", null)]
        [InlineData("undock", "x", "partaction-converter-arg-unexpected", null)]
        public void ParseConverter_OnlyTheConverterButtonsTakeIt(
            string token, string raw, string reason, string expected)
        {
            Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
            Assert.Equal(reason, TestCommandPartAction.ParseConverter(raw, kind, out string name));
            Assert.Equal(expected, name);
        }

        [Fact]
        public void AcceptsConverter_IsTheStartStopPair()
        {
            foreach (string token in TestCommandPartAction.ButtonValues)
            {
                Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
                Assert.Equal(token == "startconverter" || token == "stopconverter",
                    TestCommandPartAction.AcceptsConverter(kind));
            }
        }

        [Fact]
        public void TheConverterKeyIsSpelledForTheWire()
        {
            Assert.Equal("converter", TestCommandPartAction.ConverterKey);
            ParsedCommand cmd = Cmd("button=startconverter part=MiniISRU converter=LiquidFuel");
            Assert.True(cmd.ParseOk);
            Assert.Equal("LiquidFuel", cmd.Args["converter"]);
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
        public void DecideCandidates_ConverterFilter()
        {
            // The ISRU's four converters without a filter: ambiguous, never a first pick.
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniISRU", 1, null, 0, 0, 4));
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniISRU", 1, "Lf+Ox", 1, 0, 1));
            // A name no converter carries is a spec typo, ahead of the live-state reason.
            Assert.Equal("partaction-converter-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniISRU", 1, "LfOx", 0, 0, 0));
            // A matching converter whose Start is not live (already running) is a live state.
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniISRU", 1, "Lf+Ox", 1, 0, 0));
            // The part gate stays first.
            Assert.Equal("partaction-part-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniISRU", 0, "LfOx", 0, 0, 0));
        }

        [Fact]
        public void DecideCandidates_LabProcess()
        {
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.LabProcess, "sensorThermometer", 1, null, 0, 1, 1));
            Assert.Equal("partaction-experiment-no-data", TestCommandPartAction.DecideCandidates(
                PartActionKind.LabProcess, "sensorThermometer", 1, null, 0, 1, 0));
            // A part with no experiment module at all has no such button.
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(
                PartActionKind.LabProcess, "MiniDrill", 1, null, 0, 0, 0));
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates(
                PartActionKind.LabProcess, "sensorThermometer", 2, null, 0, 2, 2));
            // Only labprocess reads the experiment count.
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(
                PartActionKind.Experiment, "sensorThermometer", 1, null, 0, 1, 0));
        }

        [Fact]
        public void DecideCandidates_InspectCountsTheNamedParts()
        {
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.Inspect, "Large.Crewed.Lab", 1, null, 0, 0, 1));
            Assert.Equal("partaction-part-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.Inspect, "Large.Crewed.Lab", 0, null, 0, 0, 0));
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates(
                PartActionKind.Inspect, "Large.Crewed.Lab", 2, null, 0, 0, 2));
        }

        [Fact]
        public void EventNames_AreTheStockPartMenuMethods()
        {
            Assert.Equal(new[] { "Undock" }, TestCommandPartAction.EventNamesFor(PartActionKind.Undock));
            Assert.Equal(new[] { "Decouple" }, TestCommandPartAction.EventNamesFor(PartActionKind.Decouple));
            Assert.Equal(new[] { "MakeReference", "MakeReferenceTransform" },
                TestCommandPartAction.EventNamesFor(PartActionKind.ControlFrom));
            Assert.Equal(new[] { "DeployModule" }, TestCommandPartAction.EventNamesFor(PartActionKind.Deploy));
            Assert.Equal(new[] { "RetractModule" }, TestCommandPartAction.EventNamesFor(PartActionKind.Retract));
            Assert.Equal(new[] { "StartResourceConverter" },
                TestCommandPartAction.EventNamesFor(PartActionKind.StartConverter));
            Assert.Equal(new[] { "StopResourceConverter" },
                TestCommandPartAction.EventNamesFor(PartActionKind.StopConverter));
            Assert.Equal(new[] { "DeployExperiment" }, TestCommandPartAction.EventNamesFor(PartActionKind.Experiment));
            Assert.Equal(new[] { "TransmitScience" }, TestCommandPartAction.EventNamesFor(PartActionKind.LabTransmit));
            // labprocess calls a private method, not a KSPEvent; inspect presses nothing.
            Assert.Empty(TestCommandPartAction.EventNamesFor(PartActionKind.LabProcess));
            Assert.Empty(TestCommandPartAction.EventNamesFor(PartActionKind.Inspect));
            Assert.Empty(TestCommandPartAction.EventNamesFor(PartActionKind.Unknown));
            Assert.Equal("sendDataToLab", TestCommandPartAction.LabProcessMethodName);
            Assert.Equal("OnSendToLab", TestCommandPartAction.LabProcessDialogEventName);
        }

        // ----- state -----

        [Fact]
        public void FormatState_OneSegmentPerReading_InModuleOrder()
        {
            var readings = new List<PartActionModuleReading>
            {
                PartActionModuleReading.AnimationGroup(true),
                PartActionModuleReading.Converter("Surface Harvester", true),
            };
            Assert.Equal("deployed=1;Surface Harvester:active=1", TestCommandPartAction.FormatState(readings));
            Assert.Equal("Lf+Ox:active=0;LiquidFuel:active=1;Oxidizer:active=0;Monoprop:active=0",
                TestCommandPartAction.FormatState(new List<PartActionModuleReading>
                {
                    PartActionModuleReading.Converter("Lf+Ox", false),
                    PartActionModuleReading.Converter("LiquidFuel", true),
                    PartActionModuleReading.Converter("Oxidizer", false),
                    PartActionModuleReading.Converter("Monoprop", false),
                }));
            Assert.Equal("exp:data=1;deployed=0", TestCommandPartAction.FormatState(new List<PartActionModuleReading>
            {
                PartActionModuleReading.Experiment(1),
                PartActionModuleReading.AnimationGroup(false),
            }));
        }

        [Fact]
        public void FormatState_LabIsTwoDecimals_Invariant()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal("Research:active=1;lab:storedScience=12.35 dataStored=0.00",
                    TestCommandPartAction.FormatState(new List<PartActionModuleReading>
                    {
                        PartActionModuleReading.Converter("Research", true),
                        PartActionModuleReading.Lab(12.345f, 0f),
                    }));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void FormatState_EmptyForNoReadings()
        {
            Assert.Equal(string.Empty, TestCommandPartAction.FormatState(null));
            Assert.Equal(string.Empty, TestCommandPartAction.FormatState(new List<PartActionModuleReading>()));
            Assert.Equal(":active=0", TestCommandPartAction.FormatState(new List<PartActionModuleReading>
            {
                PartActionModuleReading.Converter(null, false),
            }));
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
                "vesselsBefore", "vesselsAfter", "activePid", "activeName", "converter", "state" },
                payload.Select(kv => kv.Key));
            Assert.Equal("undock", Value(payload, "button"));
            Assert.Equal("31337", Value(payload, "newPid"));
            Assert.Equal("VN4 Tug", Value(payload, "newName"));
            Assert.Equal("Kerbal X", Value(payload, "activeName"));
            Assert.Equal(string.Empty, Value(payload, "converter"));
            Assert.Equal(string.Empty, Value(payload, "state"));
        }

        [Fact]
        public void Payload_CarriesTheConverterAndTheState()
        {
            var payload = TestCommandPartAction.BuildOkPayload(PartActionKind.StartConverter, 7, "MiniISRU",
                55, "StartResourceConverter", 0, null, 1, 1, 7, "ISRU Rover", "Lf+Ox",
                "Lf+Ox:active=1;LiquidFuel:active=0");
            Assert.Equal("startconverter", Value(payload, "button"));
            Assert.Equal("Lf+Ox", Value(payload, "converter"));
            Assert.Equal("Lf+Ox:active=1;LiquidFuel:active=0", Value(payload, "state"));
            Assert.Equal("0", Value(payload, "newPid"));
        }

        [Fact]
        public void AppliedLine_IsGrepStable()
        {
            string line = TestCommandPartAction.FormatAppliedLine(PartActionKind.ControlFrom, 2200110033,
                "probeCoreOcto2.v2", 77, "MakeReference", 0, null, 1, 1, 2200110033, "VN8 Alpha");
            Assert.Equal("partaction applied button=controlfrom pid=2200110033 part='probeCoreOcto2.v2' partId=77 " +
                "event=MakeReference newPid=0 newName='' vessels=1->1 activePid=2200110033 activeName='VN8 Alpha' " +
                "converter='' state=''",
                line);
        }

        [Fact]
        public void AppliedLine_AppendsTheConverterAndTheState()
        {
            string line = TestCommandPartAction.FormatAppliedLine(PartActionKind.Inspect, 42,
                "Large.Crewed.Lab", 9, string.Empty, 0, null, 1, 1, 42, "ISRU Rover", null,
                "Research:active=0;lab:storedScience=3.10 dataStored=25.00");
            Assert.Equal("partaction applied button=inspect pid=42 part='Large.Crewed.Lab' partId=9 event= " +
                "newPid=0 newName='' vessels=1->1 activePid=42 activeName='ISRU Rover' converter='' " +
                "state='Research:active=0;lab:storedScience=3.10 dataStored=25.00'", line);
            Assert.StartsWith("partaction applied button=startconverter pid=42 part='MiniISRU' partId=9 " +
                "event=StartResourceConverter ", TestCommandPartAction.FormatAppliedLine(
                    PartActionKind.StartConverter, 42, "MiniISRU", 9, "StartResourceConverter", 0, null, 1, 1,
                    42, "ISRU Rover", "Lf+Ox", "Lf+Ox:active=1"));
            Assert.EndsWith(" converter='Lf+Ox' state='Lf+Ox:active=1'", TestCommandPartAction.FormatAppliedLine(
                PartActionKind.StartConverter, 42, "MiniISRU", 9, "StartResourceConverter", 0, null, 1, 1,
                42, "ISRU Rover", "Lf+Ox", "Lf+Ox:active=1"));
        }

        [Fact]
        public void Reasons_AreTheGateOrder_AndDisjointFromTheErrors()
        {
            Assert.Equal(15, TestCommandPartAction.Reasons.Length);
            Assert.Equal("partaction-button-arg-missing", TestCommandPartAction.Reasons[0]);
            Assert.Equal("partaction-converter-arg-unexpected", TestCommandPartAction.Reasons[3]);
            Assert.Equal("partaction-converter-not-found", TestCommandPartAction.Reasons[10]);
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.Reasons[12]);
            Assert.Equal("partaction-lab-method-missing", TestCommandPartAction.Reasons[14]);
            Assert.Equal(TestCommandPartAction.Reasons.Length, TestCommandPartAction.Reasons.Distinct().Count());
            foreach (string r in new[]
            {
                TestCommandPartAction.ConverterArgUnexpectedReason, TestCommandPartAction.ConverterNotFoundReason,
                TestCommandPartAction.ExperimentNoDataReason, TestCommandPartAction.LabMethodMissingReason,
            })
                Assert.Contains(r, TestCommandPartAction.Reasons);
            Assert.Empty(TestCommandPartAction.Reasons.Intersect(TestCommandPartAction.ErrorReasons));
            Assert.All(TestCommandPartAction.Reasons, r => Assert.StartsWith("partaction-", r));
        }
    }
}
