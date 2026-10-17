using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>ReadVesselResources (pid= | rec=) [resources=]
    /// [expect=] [tolerance=] [expectSnapshot=]</c>: a read of what is actually in the tanks
    /// of live vessels, the resource-conservation check a lane gates on.
    /// </summary>
    public class TestCommandReadVesselResourcesTests
    {
        private const string Verb = "ReadVesselResources";

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

        private static bool ValidId(string id) => RecordingPaths.ValidateRecordingId(id, RecordingIdValidationLogContext.Test);

        private static VesselResourceReading Reading(string rec, uint pid, params (string, double, double)[] tanks)
        {
            var r = new VesselResourceReading { RecordingId = rec, Found = true, Pid = pid, Name = "Kerbal X", Parts = 1 };
            r.PartUids.Add(pid * 10);
            foreach (var (name, amount, max) in tanks)
                TestCommandReadVesselResources.Accumulate(r.Resources, name, amount, max);
            return r;
        }

        // The bdock-recorded fixture's two undock tips (decoded from their _vessel.craft
        // sidecars): the Station gave the Interceptor 15.772 LiquidFuel while docked.
        private static VesselResourceReading Station() => Reading("37d0dc074351408ba0374230793abb1c", 3620499050u,
            ("LiquidFuel", 704.2278666243137, 720), ("Oxidizer", 880, 880), ("MonoPropellant", 229.99746683249214, 230),
            ("ElectricCharge", 949.98, 950));
        private static VesselResourceReading Interceptor() => Reading("4af6cfd725d646ccbac9ef2f7749667e", 1223410921u,
            ("LiquidFuel", 720, 720), ("Oxidizer", 860.7229471678664, 880), ("MonoPropellant", 207.59332674726704, 230),
            ("ElectricCharge", 949.40, 950));

        // ----- table rows -----

        [Fact]
        public void TheVerbIsImplemented_VesselListScenes_DefaultBudget_AndReadOnly()
        {
            Assert.Equal(TestCommandReadVesselResources.Verb, Verb);
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.Equal(VerbSceneRequirement.RequiresVesselListScene, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.False(TestCommandVerbs.IsStateMutatingVerb(Verb));
        }

        [Theory]
        [InlineData("Flight")]
        [InlineData("SpaceCenter")]
        [InlineData("TrackingStation")]
        public void Dispatch_ExecutesInEveryVesselListScene(string sceneName)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            var st = At(scene);
            st.Recording = scene == TestCommandScene.Flight;
            st.HasTree = st.Recording;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("pid=42"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Theory]
        [InlineData("MainMenu")]
        [InlineData("Editor")]
        [InlineData("Other")]
        public void Dispatch_DefersWhereNoVesselListExists(string sceneName)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            var r = TestCommandDispatcher.DecideDispatch(Cmd("pid=42"), At(scene));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-in-vessel-list-scene", r.Reason);
        }

        [Fact]
        public void RequiresVesselListScene_IsExactlyReadVesselResources()
        {
            var verbs = TestCommandDispatcher.PreconditionTable
                .Where(kv => kv.Value == VerbSceneRequirement.RequiresVesselListScene)
                .Select(kv => kv.Key).ToList();
            Assert.Equal(new[] { Verb }, verbs);
        }

        // ----- arg parsing -----

        [Theory]
        [InlineData(null, null, "readvesselresources-target-arg-missing")]
        [InlineData("1", "37d0dc074351408ba0374230793abb1c", "readvesselresources-target-arg-conflict")]
        [InlineData("0", null, "readvesselresources-pid-arg-invalid")]
        [InlineData("12,x", null, "readvesselresources-pid-arg-invalid")]
        [InlineData("-3", null, "readvesselresources-pid-arg-invalid")]
        [InlineData("1,", null, "readvesselresources-pid-arg-invalid")]
        [InlineData(null, "../evil", "readvesselresources-rec-arg-invalid")]
        [InlineData("1,2,3,4,5,6,7,8,9", null, "readvesselresources-too-many-targets")]
        public void ParseTargets_Refusals(string pid, string rec, string expected)
        {
            Assert.Equal(expected, TestCommandReadVesselResources.ParseTargets(pid, rec, ValidId, out _, out _));
        }

        [Fact]
        public void ParseTargets_PidsAndRecsInOrder()
        {
            Assert.Null(TestCommandReadVesselResources.ParseTargets("3620499050,1223410921", null, ValidId,
                out List<uint> pids, out List<string> recs));
            Assert.Equal(new uint[] { 3620499050u, 1223410921u }, pids);
            Assert.Null(recs);
            Assert.Null(TestCommandReadVesselResources.ParseTargets(null,
                "37d0dc074351408ba0374230793abb1c,4af6cfd725d646ccbac9ef2f7749667e", ValidId, out pids, out recs));
            Assert.Null(pids);
            Assert.Equal(2, recs.Count);
            Assert.Equal("4af6cfd725d646ccbac9ef2f7749667e", recs[1]);
        }

        [Theory]
        [InlineData("LiquidFuel,", "readvesselresources-resources-arg-invalid")]
        [InlineData("Liquid Fuel", "readvesselresources-resources-arg-invalid")]
        [InlineData("LiquidFuel,LiquidFuel", "readvesselresources-resources-arg-invalid")]
        [InlineData("LiquidFuel,MonoPropellant", null)]
        [InlineData(null, null)]
        public void ParseResources(string raw, string expected)
        {
            Assert.Equal(expected, TestCommandReadVesselResources.ParseResources(raw, out _));
        }

        [Theory]
        [InlineData("LiquidFuel", "readvesselresources-expect-arg-invalid")]
        [InlineData("LiquidFuel:", "readvesselresources-expect-arg-invalid")]
        [InlineData(":3", "readvesselresources-expect-arg-invalid")]
        [InlineData("LiquidFuel:-1", "readvesselresources-expect-arg-invalid")]
        [InlineData("LiquidFuel:1,5", "readvesselresources-expect-arg-invalid")]
        [InlineData("LiquidFuel:1e3", "readvesselresources-expect-arg-invalid")]
        [InlineData("LiquidFuel:1;LiquidFuel:2", "readvesselresources-expect-arg-invalid")]
        [InlineData("LiquidFuel:1424.2279;Oxidizer:1740.7229", null)]
        public void ParseExpect(string raw, string expected)
        {
            Assert.Equal(expected, TestCommandReadVesselResources.ParseExpect(raw, out _));
        }

        [Fact]
        public void ParseExpect_KeepsWrittenOrderAndParsesInvariantly()
        {
            using (new CultureScope("de-DE"))
            {
                Assert.Null(TestCommandReadVesselResources.ParseExpect("Oxidizer:1740.7229;LiquidFuel:1424.5",
                    out var expect));
                Assert.Equal("Oxidizer", expect[0].Key);
                Assert.Equal(1740.7229, expect[0].Value, 6);
                Assert.Equal(1424.5, expect[1].Value, 6);
            }
        }

        [Theory]
        [InlineData(null, 0.01, null)]
        [InlineData("0.5", 0.5, null)]
        [InlineData("0", 0.0, null)]
        [InlineData("-0.1", 0.01, "readvesselresources-tolerance-arg-invalid")]
        [InlineData("abc", 0.01, "readvesselresources-tolerance-arg-invalid")]
        public void ParseTolerance(string raw, double value, string expected)
        {
            Assert.Equal(expected, TestCommandReadVesselResources.ParseTolerance(raw, out double t));
            if (expected == null) Assert.Equal(value, t, 9);
        }

        [Theory]
        [InlineData(null, true, null, false)]
        [InlineData("false", false, null, false)]
        [InlineData("true", true, null, true)]
        [InlineData("True", true, "readvesselresources-expect-snapshot-arg-invalid", false)]
        [InlineData("true", false, "readvesselresources-expect-snapshot-needs-rec", true)]
        public void ParseExpectSnapshot(string raw, bool hasRec, string expected, bool value)
        {
            Assert.Equal(expected, TestCommandReadVesselResources.ParseExpectSnapshot(raw, hasRec, out bool v));
            Assert.Equal(value, v);
        }

        [Fact]
        public void ReasonsArrayHoldsEveryRefusalConstantInGateOrder()
        {
            var r = TestCommandReadVesselResources.Reasons;
            Assert.Equal(13, r.Length);
            Assert.Equal(r.Length, r.Distinct().Count());
            Assert.Equal(TestCommandReadVesselResources.TargetArgMissingReason, r[0]);
            Assert.Equal(TestCommandReadVesselResources.TargetIsGhostReason, r[r.Length - 1]);
            Assert.All(r, x => Assert.StartsWith("readvesselresources-", x));
        }

        // ----- conservation -----

        [Fact]
        public void TheDockedPairSumsToWhatItDockedWith_SoTheTransferConservedFuel()
        {
            var readings = new List<VesselResourceReading> { Station(), Interceptor() };
            var sum = TestCommandReadVesselResources.SumReadings(readings);
            TestCommandReadVesselResources.ParseExpect(
                "LiquidFuel:1424.2279;Oxidizer:1740.7229;MonoPropellant:437.5956", out var expect);
            var rows = TestCommandReadVesselResources.CompareExpect(expect, sum, 0.01, missing: 0);
            Assert.All(rows, c => Assert.True(c.Match, c.Resource));
            Assert.Equal(1424.2279, rows[0].Actual, 3);
        }

        [Fact]
        public void FuelThatAppearsOrVanishesFailsTheTotal()
        {
            var station = Station();
            TestCommandReadVesselResources.Accumulate(station.Resources, "LiquidFuel", 15.772, 0);
            var sum = TestCommandReadVesselResources.SumReadings(new List<VesselResourceReading> { station, Interceptor() });
            TestCommandReadVesselResources.ParseExpect("LiquidFuel:1424.2279;Oxidizer:1740.7229", out var expect);
            var rows = TestCommandReadVesselResources.CompareExpect(expect, sum, 0.01, 0);
            Assert.False(rows[0].Match);
            Assert.True(rows[1].Match);
            var payload = TestCommandReadVesselResources.BuildOkPayload(
                new List<VesselResourceReading> { station, Interceptor() },
                new List<string> { "LiquidFuel" }, sum, 0, true, rows, false, true, null);
            Assert.Equal("false", Value(payload, "match"));
            Assert.Equal("LiquidFuel", Value(payload, "mismatches"));
        }

        [Fact]
        public void AMissingVesselFailsEveryExpectation()
        {
            var missing = new VesselResourceReading { RecordingId = "4af6cfd725d646ccbac9ef2f7749667e", Found = false };
            var readings = new List<VesselResourceReading> { Station(), missing };
            var sum = TestCommandReadVesselResources.SumReadings(readings);
            TestCommandReadVesselResources.ParseExpect("LiquidFuel:704.2279", out var expect);
            var rows = TestCommandReadVesselResources.CompareExpect(expect, sum, 0.01, missing: 1);
            Assert.False(rows[0].Match);
            Assert.Equal(704.2279, rows[0].Actual, 3);
        }

        [Fact]
        public void AResourceNoVesselCarriesReadsZero()
        {
            var sum = TestCommandReadVesselResources.SumReadings(new List<VesselResourceReading> { Station() });
            TestCommandReadVesselResources.ParseExpect("Ore:0;XenonGas:5", out var expect);
            var rows = TestCommandReadVesselResources.CompareExpect(expect, sum, 0.01, 0);
            Assert.True(rows[0].Match);
            Assert.False(rows[1].Match);
            Assert.Equal(0, rows[1].Actual);
        }

        // ----- snapshot comparison -----

        [Fact]
        public void SnapshotTotals_SumsEveryPartResource()
        {
            var vessel = new ConfigNode("VESSEL");
            var p1 = vessel.AddNode("PART");
            var r1 = p1.AddNode("RESOURCE");
            r1.AddValue("name", "LiquidFuel"); r1.AddValue("amount", "344.2278666243137"); r1.AddValue("maxAmount", "360");
            var r2 = p1.AddNode("RESOURCE");
            r2.AddValue("name", "ElectricCharge"); r2.AddValue("amount", "50"); r2.AddValue("maxAmount", "50");
            var p2 = vessel.AddNode("PART");
            var r3 = p2.AddNode("RESOURCE");
            r3.AddValue("name", "LiquidFuel"); r3.AddValue("amount", "360"); r3.AddValue("maxAmount", "360");
            var totals = TestCommandReadVesselResources.SnapshotTotals(vessel);
            Assert.Equal(704.2278666243137, totals["LiquidFuel"].Amount, 9);
            Assert.Equal(720, totals["LiquidFuel"].MaxAmount, 9);
            Assert.Equal(50, totals["ElectricCharge"].Amount, 9);
            Assert.Null(TestCommandReadVesselResources.SnapshotTotals(null));
            Assert.Null(TestCommandReadVesselResources.SnapshotTotals(new ConfigNode("VESSEL")));
        }

        [Fact]
        public void SnapshotCompare_SkipsElectricChargeUnlessAsked()
        {
            var r = Station();
            r.Snapshot = new Dictionary<string, ResourceReading>(r.Resources);
            TestCommandReadVesselResources.Accumulate(r.Snapshot, "ElectricCharge", 0.5, 0);
            var rows = TestCommandReadVesselResources.CompareSnapshot(null, r, 0.01);
            Assert.DoesNotContain(rows, c => c.Resource == "ElectricCharge");
            Assert.All(rows, c => Assert.True(c.Match));
            rows = TestCommandReadVesselResources.CompareSnapshot(new List<string> { "ElectricCharge" }, r, 0.01);
            Assert.False(Assert.Single(rows).Match);
        }

        [Fact]
        public void FoldSnapshot_NamesTheRecordingAndResourceOfEveryMismatch()
        {
            var station = Station();
            station.Snapshot = new Dictionary<string, ResourceReading>(station.Resources);
            var interceptor = Interceptor();
            interceptor.Snapshot = new Dictionary<string, ResourceReading>(interceptor.Resources);
            TestCommandReadVesselResources.Accumulate(interceptor.Snapshot, "LiquidFuel", -15.772, 0);
            var noSnap = Reading("aaaa", 7);
            var missing = new VesselResourceReading { RecordingId = "bbbb", Found = false };
            bool ok = TestCommandReadVesselResources.FoldSnapshot(
                new List<VesselResourceReading> { station, interceptor, noSnap, missing }, null, 0.01, out string list);
            Assert.False(ok);
            Assert.Equal("4af6cfd725d646ccbac9ef2f7749667e:LiquidFuel,aaaa:no-snapshot,bbbb:missing", list);
            Assert.True(TestCommandReadVesselResources.FoldSnapshot(
                new List<VesselResourceReading> { station }, null, 0.01, out list));
            Assert.Equal(string.Empty, list);
        }

        // ----- duplicates -----

        [Fact]
        public void UidCollisions_CountsAPartCarriedTwiceOrByAnotherLiveVessel()
        {
            var a = Station();
            var b = Interceptor();
            Assert.Equal(0, TestCommandReadVesselResources.CountUidCollisions(
                new List<VesselResourceReading> { a, b }, new HashSet<uint> { 1u, 2u }));
            Assert.Equal(1, TestCommandReadVesselResources.CountUidCollisions(
                new List<VesselResourceReading> { a, b }, new HashSet<uint> { a.PartUids[0] }));
            var twin = Station();
            Assert.Equal(1, TestCommandReadVesselResources.CountUidCollisions(
                new List<VesselResourceReading> { a, twin }, new HashSet<uint>()));
        }

        [Fact]
        public void UidDigest_IsOrderFreeEightHex()
        {
            string d1 = TestCommandReadVesselResources.UidDigest(new List<VesselResourceReading> { Station(), Interceptor() });
            string d2 = TestCommandReadVesselResources.UidDigest(new List<VesselResourceReading> { Interceptor(), Station() });
            Assert.Equal(d1, d2);
            Assert.Matches("^[0-9a-f]{8}$", d1);
            Assert.NotEqual(d1, TestCommandReadVesselResources.UidDigest(new List<VesselResourceReading> { Station() }));
        }

        // ----- answer shape -----

        [Fact]
        public void Payload_CarriesSummedTotalsAsReferenceableFields()
        {
            using (new CultureScope("ro-RO"))
            {
                var readings = new List<VesselResourceReading> { Station(), Interceptor() };
                var sum = TestCommandReadVesselResources.SumReadings(readings);
                var reported = TestCommandReadVesselResources.ReportedResources(
                    new List<string> { "LiquidFuel", "MonoPropellant" }, sum);
                var p = TestCommandReadVesselResources.BuildOkPayload(readings, reported, sum, 0,
                    false, new List<ResourceComparison>(), false, true, null);
                Assert.Equal("2", Value(p, "vessels"));
                Assert.Equal("3620499050,1223410921", Value(p, "pids"));
                Assert.Equal("0", Value(p, "missing"));
                Assert.Equal("LiquidFuel,MonoPropellant", Value(p, "resources"));
                Assert.Equal("1424.2279", Value(p, "LiquidFuel"));
                Assert.Equal("1440.0000", Value(p, "LiquidFuelMax"));
                Assert.Equal("437.5908", Value(p, "MonoPropellant"));
                Assert.Equal("none", Value(p, "match"));
                Assert.Equal("none", Value(p, "snapshotMatch"));
                Assert.DoesNotContain(p, kv => kv.Key == "Oxidizer");
            }
        }

        [Fact]
        public void ReportedResources_DefaultsToEverythingFoundSorted()
        {
            var sum = TestCommandReadVesselResources.SumReadings(new List<VesselResourceReading> { Station() });
            Assert.Equal(new[] { "ElectricCharge", "LiquidFuel", "MonoPropellant", "Oxidizer" },
                TestCommandReadVesselResources.ReportedResources(null, sum));
        }

        [Fact]
        public void Lines_AreGrepStableAndInvariant()
        {
            using (new CultureScope("de-DE"))
            {
                var station = Station();
                Assert.Equal(
                    "readvesselresources vessel index=0 rec=37d0dc074351408ba0374230793abb1c found=true pid=3620499050 "
                    + "name='Kerbal X' loaded=true parts=1 LiquidFuel=704.2279/720.0000",
                    TestCommandReadVesselResources.FormatVesselLine(0,
                        new VesselResourceReading
                        {
                            RecordingId = station.RecordingId, Found = true, Pid = station.Pid, Name = "Kerbal X",
                            Loaded = true, Parts = 1, Resources = station.Resources,
                        },
                        new List<string> { "LiquidFuel" }));
                Assert.Equal("readvesselresources vessel index=1 rec=bbbb found=false",
                    TestCommandReadVesselResources.FormatVesselLine(1,
                        new VesselResourceReading { RecordingId = "bbbb", Found = false }, null));
                var c = new ResourceComparison { Resource = "LiquidFuel", Expected = 1424.2279, Actual = 1424.22794, Match = true };
                Assert.Equal("readvesselresources expect resource=LiquidFuel expected=1424.2279 actual=1424.2279 "
                    + "diff=0.0000 tolerance=0.0100 match=true",
                    TestCommandReadVesselResources.FormatExpectLine(c, 0.01));
                Assert.Equal("readvesselresources complete vessels=2 missing=0 uidCollisions=0 uidDigest=0123abcd "
                    + "match=true snapshotMatch=none",
                    TestCommandReadVesselResources.FormatCompleteLine(2, 0, 0, "0123abcd", "true", "none"));
            }
        }

        private sealed class CultureScope : System.IDisposable
        {
            private readonly CultureInfo previous;

            internal CultureScope(string name)
            {
                previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(name);
            }

            public void Dispose() => System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
