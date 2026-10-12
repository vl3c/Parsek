using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>RenameVessel name= [pid=]</c>: a player's accept in
    /// the stock rename dialog for one live vessel (vessel names, design 15.3).
    /// </summary>
    public class TestCommandRenameVesselTests
    {
        private const string Verb = "RenameVessel";

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

        private static VesselNameDecision Decision(long seq, string name, VesselNameSource source)
            => new VesselNameDecision { Seq = seq, Name = name, Source = source };

        // ----- table rows -----

        [Fact]
        public void TheVerbIsImplemented_VesselScenes_DefaultBudget_AndMutating()
        {
            Assert.Equal(TestCommandRenameVessel.Verb, Verb);
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.Equal(VerbSceneRequirement.RequiresVesselScene, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(Verb));
        }

        [Fact]
        public void Dispatch_ExecutesInFlight_EvenWithALiveRecorder()
        {
            // A live recorder is the ordinary case: the capture needs a recorded vessel.
            var st = At(TestCommandScene.Flight);
            st.Recording = true;
            st.HasTree = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("name=VN"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_ExecutesInTheTrackingStation()
        {
            // The Tracking Station's vessel info header renames an unloaded vessel.
            var r = TestCommandDispatcher.DecideDispatch(Cmd("name=VN pid=42"), At(TestCommandScene.TrackingStation));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Theory]
        [InlineData("SpaceCenter")]
        [InlineData("MainMenu")]
        [InlineData("Editor")]
        [InlineData("Other")]
        public void Dispatch_DefersOutsideFlightAndTheTrackingStation(string sceneName)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            var r = TestCommandDispatcher.DecideDispatch(Cmd("name=VN"), At(scene));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal(TestCommandDispatcher.NotInVesselSceneReason, r.Reason);
            Assert.Equal("not-in-vessel-scene", r.Reason);
        }

        [Fact]
        public void IsVesselScene_IsExactlyFlightAndTheTrackingStation()
        {
            foreach (TestCommandScene scene in System.Enum.GetValues(typeof(TestCommandScene)))
            {
                bool expected = scene == TestCommandScene.Flight || scene == TestCommandScene.TrackingStation;
                Assert.Equal(expected, TestCommandDispatcher.IsVesselScene(scene));
            }
        }

        [Fact]
        public void RequiresVesselScene_IsExactlyRenameVessel()
        {
            int rows = 0;
            foreach (var kv in TestCommandDispatcher.PreconditionTable)
            {
                if (kv.Value != VerbSceneRequirement.RequiresVesselScene) continue;
                rows++;
                Assert.Equal(Verb, kv.Key);
            }
            Assert.Equal(1, rows);
        }

        [Fact]
        public void APercentEncodedNameWithSpacesReachesTheVerbDecoded()
        {
            ParsedCommand cmd = Cmd("name=VN1%20Alpha%20Probe pid=42");
            Assert.True(cmd.ParseOk);
            Assert.Equal("VN1 Alpha Probe", cmd.Args["name"]);
            Assert.Null(TestCommandRenameVessel.ParseName(cmd.Args["name"], out string name));
            Assert.Equal("VN1 Alpha Probe", name);
        }

        // ----- args -----

        [Theory]
        [InlineData(null, "renamevessel-name-arg-missing")]
        [InlineData("", "renamevessel-name-arg-missing")]
        [InlineData(" ", "renamevessel-name-arg-invalid")]
        [InlineData("\t  ", "renamevessel-name-arg-invalid")]
        public void ParseName_RefusesAbsentAndBlank(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandRenameVessel.ParseName(raw, out string name));
            Assert.Null(name);
        }

        [Theory]
        [InlineData("VN1 Alpha")]
        [InlineData(" padded ")]
        [InlineData("#autoLOC_501232")]
        public void ParseName_PassesTheNameVerbatim(string raw)
        {
            Assert.Null(TestCommandRenameVessel.ParseName(raw, out string name));
            Assert.Equal(raw, name);
        }

        [Fact]
        public void ParsePid_AbsentMeansTheActiveVessel()
        {
            Assert.Null(TestCommandRenameVessel.ParsePid(null, out uint pid, out bool hasPid));
            Assert.False(hasPid);
            Assert.Equal(0u, pid);
            Assert.Null(TestCommandRenameVessel.ParsePid("", out pid, out hasPid));
            Assert.False(hasPid);
        }

        [Theory]
        [InlineData("1", 1u)]
        [InlineData("2200110033", 2200110033u)]
        [InlineData("4294967295", 4294967295u)]
        public void ParsePid_AcceptsAPositiveDecimalUint(string raw, uint expected)
        {
            Assert.Null(TestCommandRenameVessel.ParsePid(raw, out uint pid, out bool hasPid));
            Assert.True(hasPid);
            Assert.Equal(expected, pid);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("+5")]
        [InlineData(" 5")]
        [InlineData("abc")]
        [InlineData("4294967296")]
        [InlineData("1.5")]
        public void ParsePid_RefusesAnythingElse(string raw)
        {
            Assert.Equal(TestCommandRenameVessel.PidArgInvalidReason,
                TestCommandRenameVessel.ParsePid(raw, out uint pid, out bool hasPid));
            Assert.False(hasPid);
            Assert.Equal(0u, pid);
        }

        // ----- target gate -----

        [Fact]
        public void DecideTarget_RefusesInGateOrder()
        {
            Assert.Equal("renamevessel-vessel-not-found",
                TestCommandRenameVessel.DecideTarget(true, false, true, false));
            Assert.Equal("renamevessel-no-active-vessel",
                TestCommandRenameVessel.DecideTarget(false, false, true, false));
            Assert.Equal("renamevessel-target-is-ghost",
                TestCommandRenameVessel.DecideTarget(true, true, true, false));
            Assert.Equal("renamevessel-accept-unavailable",
                TestCommandRenameVessel.DecideTarget(false, true, false, false));
            Assert.Null(TestCommandRenameVessel.DecideTarget(true, true, false, true));
            Assert.Null(TestCommandRenameVessel.DecideTarget(false, true, false, true));
        }

        [Fact]
        public void EveryGateReasonIsInTheReasonsTable_InOrder()
        {
            Assert.Equal(new[]
            {
                TestCommandRenameVessel.NameArgMissingReason,
                TestCommandRenameVessel.NameArgInvalidReason,
                TestCommandRenameVessel.PidArgInvalidReason,
                TestCommandRenameVessel.NoActiveVesselReason,
                TestCommandRenameVessel.VesselNotFoundReason,
                TestCommandRenameVessel.TargetIsGhostReason,
                TestCommandRenameVessel.AcceptUnavailableReason,
            }, TestCommandRenameVessel.Reasons);
            Assert.Equal(new[]
            {
                TestCommandRenameVessel.AcceptThrewReason,
                TestCommandRenameVessel.NotAppliedReason,
            }, TestCommandRenameVessel.ErrorReasons);
            Assert.Empty(TestCommandRenameVessel.Reasons.Intersect(TestCommandRenameVessel.ErrorReasons));
        }

        [Fact]
        public void WasApplied_IsAnOrdinalCompare()
        {
            Assert.True(TestCommandRenameVessel.WasApplied("VN", "VN"));
            Assert.False(TestCommandRenameVessel.WasApplied("VN", "vn"));
            Assert.False(TestCommandRenameVessel.WasApplied("VN", null));
        }

        // ----- decision read -----

        [Fact]
        public void PickDecision_TakesTheHighestSeqOverTheIdentityGuids()
        {
            var byGuid = new Dictionary<string, VesselNameDecision>
            {
                ["a"] = Decision(3, "Old", VesselNameSource.StockDialog),
                ["b"] = Decision(7, "New", VesselNameSource.ParsekRename),
            };
            VesselNameDecision d = TestCommandRenameVessel.PickDecision(
                new[] { "a", "missing", "b" }, g => byGuid.TryGetValue(g, out var x) ? x : null);
            Assert.Equal(7L, d.Seq);
            Assert.Equal("New", d.Name);
            Assert.Null(TestCommandRenameVessel.PickDecision(new[] { "missing" }, g => null));
            Assert.Null(TestCommandRenameVessel.PickDecision(null, g => null));
        }

        // ----- answer -----

        [Fact]
        public void BuildOkPayload_CarriesTheOldAndNewNamesAndTheDecision()
        {
            List<KeyValuePair<string, string>> p = TestCommandRenameVessel.BuildOkPayload(
                2200110033u, "GS1 Auto-Chute Booster", "VN1 Alpha", "Ship", true, 1,
                Decision(4, "VN1 Alpha", VesselNameSource.StockDialog));
            Assert.Equal(new[] { "pid", "old", "new", "type", "active", "changed", "legs", "decision", "decisionName" },
                p.Select(kv => kv.Key).ToArray());
            Assert.Equal("2200110033", Value(p, "pid"));
            Assert.Equal("GS1 Auto-Chute Booster", Value(p, "old"));
            Assert.Equal("VN1 Alpha", Value(p, "new"));
            Assert.Equal("true", Value(p, "changed"));
            Assert.Equal("1", Value(p, "legs"));
            Assert.Equal("StockDialog", Value(p, "decision"));
            Assert.Equal("VN1 Alpha", Value(p, "decisionName"));
        }

        [Fact]
        public void BuildOkPayload_WithNoDecision_SaysNone()
        {
            List<KeyValuePair<string, string>> p = TestCommandRenameVessel.BuildOkPayload(
                5u, "Same", "Same", "Probe", false, 0, null);
            Assert.Equal("false", Value(p, "changed"));
            Assert.Equal("false", Value(p, "active"));
            Assert.Equal("0", Value(p, "legs"));
            Assert.Equal("none", Value(p, "decision"));
            Assert.Equal("", Value(p, "decisionName"));
        }

        [Fact]
        public void FormatAppliedLine_IsGrepStableAndQuotesTheNames()
        {
            Assert.Equal(
                "renamevessel applied pid=42 old='VN1 Alpha' new='VN1 Bravo' type=Ship active=true " +
                "changed=true legs=2 decision=StockDialog decisionName='VN1 Bravo'",
                TestCommandRenameVessel.FormatAppliedLine(42u, "VN1 Alpha", "VN1 Bravo", "Ship", true, 2,
                    Decision(9, "VN1 Bravo", VesselNameSource.StockDialog)));
            Assert.Equal(
                "renamevessel applied pid=7 old='' new='X' type=Probe active=false changed=true " +
                "legs=0 decision=none decisionName=''",
                TestCommandRenameVessel.FormatAppliedLine(7u, null, "X", "Probe", false, 0, null));
        }
    }
}
