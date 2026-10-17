using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>PartAction button=undock|decouple|controlfrom|deploy|
    /// retract|startconverter|stopconverter|experiment|labprocess|labtransmit|inspect|radiatoron|
    /// radiatoroff [part=]
    /// [converter=] [all=true] [pid=]</c>: one button of one part's stock right-click menu (vessel names,
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
        [InlineData("radiatoron", "RadiatorOn")]
        [InlineData("radiatoroff", "RadiatorOff")]
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
        [InlineData("RadiatorOn", "partaction-button-arg-invalid")]
        [InlineData("radiator", "partaction-button-arg-invalid")]
        [InlineData("radiator-on", "partaction-button-arg-invalid")]
        public void ParseButton_RefusesAbsentAndUnknown(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandPartAction.ParseButton(raw, out PartActionKind kind));
            Assert.Equal(PartActionKind.Unknown, kind);
        }

        [Fact]
        public void ButtonValues_AreTheParsedTokens_InOrder()
        {
            Assert.Equal(new[] { "undock", "decouple", "controlfrom", "deploy", "retract", "startconverter",
                "stopconverter", "experiment", "labprocess", "labtransmit", "inspect", "radiatoron",
                "radiatoroff" },
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
        [InlineData("radiatoron", true)]
        [InlineData("radiatoroff", true)]
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
        [InlineData("radiatoron", "Lf+Ox", "partaction-converter-arg-unexpected", null)]
        public void ParseConverter_OnlyTheConverterButtonsTakeIt(
            string token, string raw, string reason, string expected)
        {
            Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
            Assert.Equal(reason, TestCommandPartAction.ParseConverter(raw, kind, out string name));
            Assert.Equal(expected, name);
        }

        [Theory]
        [InlineData("deploy", null, null, false)]
        [InlineData("deploy", "", null, false)]
        [InlineData("deploy", "true", null, true)]
        [InlineData("retract", "true", null, true)]
        [InlineData("startconverter", "true", null, true)]
        [InlineData("stopconverter", "true", null, true)]
        [InlineData("experiment", "true", null, true)]
        [InlineData("labprocess", "true", null, true)]
        [InlineData("labtransmit", "true", null, true)]
        [InlineData("inspect", "true", null, true)]
        [InlineData("radiatoron", "true", null, true)]
        [InlineData("radiatoroff", "true", null, true)]
        [InlineData("radiatoroff", "True", "partaction-all-arg-invalid", false)]
        [InlineData("undock", "true", "partaction-all-arg-unexpected", false)]
        [InlineData("decouple", "true", "partaction-all-arg-unexpected", false)]
        [InlineData("controlfrom", "true", "partaction-all-arg-unexpected", false)]
        [InlineData("undock", null, null, false)]
        [InlineData("deploy", "True", "partaction-all-arg-invalid", false)]
        [InlineData("deploy", "false", "partaction-all-arg-invalid", false)]
        [InlineData("deploy", "1", "partaction-all-arg-invalid", false)]
        [InlineData("decouple", "yes", "partaction-all-arg-invalid", false)]
        public void ParseAll_IsClosedToTrue_AndRefusedOnTheReshapingButtons(
            string token, string raw, string reason, bool expected)
        {
            Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
            Assert.Equal(reason, TestCommandPartAction.ParseAll(raw, kind, out bool all));
            Assert.Equal(expected, all);
        }

        [Fact]
        public void AcceptsAll_EveryButtonButUndockDecoupleControlFrom()
        {
            foreach (string token in TestCommandPartAction.ButtonValues)
            {
                Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
                Assert.Equal(token != "undock" && token != "decouple" && token != "controlfrom",
                    TestCommandPartAction.AcceptsAll(kind));
            }
            Assert.False(TestCommandPartAction.AcceptsAll(PartActionKind.Unknown));
            Assert.Equal("all", TestCommandPartAction.AllKey);
            ParsedCommand cmd = Cmd("button=deploy part=MiniDrill all=true");
            Assert.True(cmd.ParseOk);
            Assert.Equal("true", cmd.Args["all"]);
        }

        [Fact]
        public void DecideCandidates_AllPressesEveryCandidate_ZeroStillRefuses()
        {
            // Four drills: ambiguous without all=, every one pressed with it.
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates(
                PartActionKind.Deploy, "MiniDrill", 4, null, 0, 0, 4, false));
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.Deploy, "MiniDrill", 4, null, 0, 0, 4, true));
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.Deploy, "MiniDrill", 4, null, 0, 0, 1, true));
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(
                PartActionKind.Deploy, "MiniDrill", 4, null, 0, 0, 0, true));
            Assert.Equal("partaction-part-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.Deploy, "MiniDrill", 0, null, 0, 0, 0, true));
            Assert.Equal("partaction-converter-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniDrill", 4, "Nope", 0, 0, 0, true));
            Assert.Equal("partaction-experiment-no-data", TestCommandPartAction.DecideCandidates(
                PartActionKind.LabProcess, "sensorThermometer", 4, null, 0, 4, 0, true));
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.StartConverter, "MiniISRU", 4, null, 0, 0, 16, true));
        }

        [Fact]
        public void DecideCandidates_Radiator_NoModuleIsNotFound_AlreadyInStateIsUnavailable()
        {
            // The rover's four small radiators, all switched off: radiatoron presses all four.
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOn, "radPanelSm", 4, null, 0, 0, 4, 4, true));
            Assert.Equal("partaction-part-ambiguous", TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOn, "radPanelSm", 4, null, 0, 0, 4, 4, false));
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOff, "radPanelSm", 1, null, 0, 0, 1, 1, false));
            // A named part that carries no ModuleActiveRadiator is a spec fault.
            Assert.Equal("partaction-radiator-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOn, "MiniDrill", 4, null, 0, 0, 0, 0, true));
            Assert.Equal("partaction-radiator-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOff, "MiniDrill", 1, null, 0, 0, 0, 0, false));
            // Radiators that are there but already cooling (stock hides Activate): unavailable.
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOn, "radPanelSm", 4, null, 0, 0, 4, 0, true));
            // A part the vessel does not carry is still part-not-found first.
            Assert.Equal("partaction-part-not-found", TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOn, "radPanelSm", 0, null, 0, 0, 0, 0, true));
            // The radiator count never refuses another button, and a negative count is
            // "not counted" (the older overloads).
            Assert.Null(TestCommandPartAction.DecideCandidates(
                PartActionKind.Deploy, "MiniDrill", 1, null, 0, 0, 0, 1, false));
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.DecideCandidates(
                PartActionKind.RadiatorOn, "radPanelSm", 4, null, 0, 0, 0, true));
        }

        [Fact]
        public void IsRadiatorKind_IsTheOnOffPair()
        {
            foreach (string token in TestCommandPartAction.ButtonValues)
            {
                Assert.Null(TestCommandPartAction.ParseButton(token, out PartActionKind kind));
                Assert.Equal(token == "radiatoron" || token == "radiatoroff",
                    TestCommandPartAction.IsRadiatorKind(kind));
            }
            Assert.False(TestCommandPartAction.IsRadiatorKind(PartActionKind.Unknown));
            ParsedCommand cmd = Cmd("button=radiatoroff part=radPanelSm all=true");
            Assert.True(cmd.ParseOk);
            Assert.Equal("radiatoroff", cmd.Args["button"]);
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
            Assert.Equal(new[] { "Activate" }, TestCommandPartAction.EventNamesFor(PartActionKind.RadiatorOn));
            Assert.Equal(new[] { "Shutdown" }, TestCommandPartAction.EventNamesFor(PartActionKind.RadiatorOff));
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
        public void FormatState_RadiatorReadsItsCoolingFlag()
        {
            Assert.Equal("radiator:cooling=1", TestCommandPartAction.FormatState(new List<PartActionModuleReading>
            {
                PartActionModuleReading.Radiator(true),
            }));
            Assert.Equal("radiator:cooling=0;deployed=1", TestCommandPartAction.FormatState(new List<PartActionModuleReading>
            {
                PartActionModuleReading.Radiator(false),
                PartActionModuleReading.AnimationGroup(true),
            }));
            var parts = new List<KeyValuePair<uint, IList<PartActionModuleReading>>>
            {
                new KeyValuePair<uint, IList<PartActionModuleReading>>(11, new List<PartActionModuleReading>
                {
                    PartActionModuleReading.Radiator(true),
                }),
                new KeyValuePair<uint, IList<PartActionModuleReading>>(12, new List<PartActionModuleReading>
                {
                    PartActionModuleReading.Radiator(false),
                }),
            };
            Assert.Equal("11:radiator:cooling=1 | 12:radiator:cooling=0", TestCommandPartAction.FormatAllState(parts));
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
        public void FormatAllState_FlightIdPrefixed_PipeJoined_InGivenOrder()
        {
            var parts = new List<KeyValuePair<uint, IList<PartActionModuleReading>>>
            {
                new KeyValuePair<uint, IList<PartActionModuleReading>>(123, new List<PartActionModuleReading>
                {
                    PartActionModuleReading.Converter("Surface Harvester", true),
                    PartActionModuleReading.AnimationGroup(true),
                }),
                new KeyValuePair<uint, IList<PartActionModuleReading>>(456, new List<PartActionModuleReading>
                {
                    PartActionModuleReading.Converter("Surface Harvester", false),
                    PartActionModuleReading.AnimationGroup(false),
                }),
                new KeyValuePair<uint, IList<PartActionModuleReading>>(7, new List<PartActionModuleReading>()),
            };
            Assert.Equal("123:Surface Harvester:active=1;deployed=1 | 456:Surface Harvester:active=0;deployed=0 | 7:",
                TestCommandPartAction.FormatAllState(parts));
            Assert.Equal(string.Empty, TestCommandPartAction.FormatAllState(null));
            Assert.Equal(string.Empty, TestCommandPartAction.FormatAllState(
                new List<KeyValuePair<uint, IList<PartActionModuleReading>>>()));
        }

        [Fact]
        public void FormatPressProgress_NamesTheSucceededPresses()
        {
            Assert.Equal("presses=2/4", TestCommandPartAction.FormatPressProgress(2, 4));
            Assert.Equal("presses=0/1", TestCommandPartAction.FormatPressProgress(0, 1));
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
                "vesselsBefore", "vesselsAfter", "activePid", "activeName", "converter", "state", "count",
                "packed", "warpIndex", "warpRate", "ut" },
                payload.Select(kv => kv.Key));
            Assert.Equal("undock", Value(payload, "button"));
            Assert.Equal("31337", Value(payload, "newPid"));
            Assert.Equal("VN4 Tug", Value(payload, "newName"));
            Assert.Equal("Kerbal X", Value(payload, "activeName"));
            Assert.Equal(string.Empty, Value(payload, "converter"));
            Assert.Equal(string.Empty, Value(payload, "state"));
            Assert.Equal("1", Value(payload, "count"));
            Assert.Equal("0", Value(payload, "packed"));
            Assert.Equal("0", Value(payload, "warpIndex"));
            Assert.Equal("0", Value(payload, "warpRate"));
            Assert.Equal("0", Value(payload, "ut"));
        }

        [Fact]
        public void Payload_CarriesTheWarpContext_Invariant()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                var payload = TestCommandPartAction.BuildOkPayload(PartActionKind.StopConverter, 7, "MiniISRU",
                    55, "StopResourceConverter", 0, null, 1, 1, 7, "Miner", "Lf+Ox", "Lf+Ox:active=0", 1,
                    new PartActionWarpContext(true, 4, 100.0, 1234.5));
                Assert.Equal("1", Value(payload, "packed"));
                Assert.Equal("4", Value(payload, "warpIndex"));
                Assert.Equal("100", Value(payload, "warpRate"));
                Assert.Equal("1234.5", Value(payload, "ut"));
                Assert.Equal("ut", payload.Last().Key);
                Assert.Equal(" packed=1 warpIndex=4 warpRate=37.5 ut=1234.5",
                    TestCommandPartAction.AppendWarpContext(new PartActionWarpContext(true, 4, 37.5, 1234.5)));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void Payload_CarriesThePressCount()
        {
            var payload = TestCommandPartAction.BuildOkPayload(PartActionKind.Deploy, 7, "MiniDrill",
                123, "DeployModule", 0, null, 1, 1, 7, "Miner", null,
                "123:deployed=1 | 456:deployed=1 | 789:deployed=1 | 1011:deployed=1", 4);
            Assert.Equal("4", Value(payload, "count"));
            Assert.Equal("count", payload[13].Key);
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
                "converter='' state='' count=1 packed=0 warpIndex=0 warpRate=0 ut=0",
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
                "state='Research:active=0;lab:storedScience=3.10 dataStored=25.00' count=1 packed=0 warpIndex=0 " +
                "warpRate=0 ut=0", line);
            Assert.StartsWith("partaction applied button=startconverter pid=42 part='MiniISRU' partId=9 " +
                "event=StartResourceConverter ", TestCommandPartAction.FormatAppliedLine(
                    PartActionKind.StartConverter, 42, "MiniISRU", 9, "StartResourceConverter", 0, null, 1, 1,
                    42, "ISRU Rover", "Lf+Ox", "Lf+Ox:active=1"));
            Assert.EndsWith(" converter='Lf+Ox' state='Lf+Ox:active=1' count=1 packed=0 warpIndex=0 warpRate=0 ut=0",
                TestCommandPartAction.FormatAppliedLine(
                PartActionKind.StartConverter, 42, "MiniISRU", 9, "StartResourceConverter", 0, null, 1, 1,
                42, "ISRU Rover", "Lf+Ox", "Lf+Ox:active=1"));
            Assert.EndsWith(" state='123:deployed=1 | 456:deployed=1' count=2 packed=0 warpIndex=0 warpRate=0 ut=0",
                TestCommandPartAction.FormatAppliedLine(
                PartActionKind.Deploy, 42, "MiniDrill", 123, "DeployModule", 0, null, 1, 1,
                42, "Miner", null, "123:deployed=1 | 456:deployed=1", 2));
            // A press during a detached rails warp on the packed rover: the tail a lane anchors.
            Assert.EndsWith(" state='radiator:cooling=1' count=1 packed=1 warpIndex=5 warpRate=1000 ut=86400.25",
                TestCommandPartAction.FormatAppliedLine(
                PartActionKind.RadiatorOn, 42, "radPanelSm", 9, "Activate", 0, null, 1, 1,
                42, "Miner", null, "radiator:cooling=1", 1, new PartActionWarpContext(true, 5, 1000.0, 86400.25)));
        }

        [Fact]
        public void Reasons_AreTheGateOrder_AndDisjointFromTheErrors()
        {
            Assert.Equal(18, TestCommandPartAction.Reasons.Length);
            Assert.Equal("partaction-button-arg-missing", TestCommandPartAction.Reasons[0]);
            Assert.Equal("partaction-converter-arg-unexpected", TestCommandPartAction.Reasons[3]);
            Assert.Equal("partaction-all-arg-invalid", TestCommandPartAction.Reasons[4]);
            Assert.Equal("partaction-all-arg-unexpected", TestCommandPartAction.Reasons[5]);
            Assert.Equal("partaction-converter-not-found", TestCommandPartAction.Reasons[12]);
            Assert.Equal("partaction-event-unavailable", TestCommandPartAction.Reasons[14]);
            Assert.Equal("partaction-lab-method-missing", TestCommandPartAction.Reasons[16]);
            Assert.Equal("partaction-radiator-not-found", TestCommandPartAction.Reasons[17]);
            Assert.Equal(TestCommandPartAction.Reasons.Length, TestCommandPartAction.Reasons.Distinct().Count());
            foreach (string r in new[]
            {
                TestCommandPartAction.ConverterArgUnexpectedReason, TestCommandPartAction.ConverterNotFoundReason,
                TestCommandPartAction.ExperimentNoDataReason, TestCommandPartAction.LabMethodMissingReason,
                TestCommandPartAction.AllArgInvalidReason, TestCommandPartAction.AllArgUnexpectedReason,
                TestCommandPartAction.RadiatorNotFoundReason,
            })
                Assert.Contains(r, TestCommandPartAction.Reasons);
            Assert.Empty(TestCommandPartAction.Reasons.Intersect(TestCommandPartAction.ErrorReasons));
            Assert.All(TestCommandPartAction.Reasons, r => Assert.StartsWith("partaction-", r));
        }
    }
}
