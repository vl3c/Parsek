using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="CareerSaveParser"/> (Layer A of the ledger
    /// ground-truth harness). Fixtures are hand-built ConfigNodes shaped like a
    /// real .sfs per the verified node paths in
    /// docs/dev/design-ledger-groundtruth-harness.md.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerGroundTruthParserTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerGroundTruthParserTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Fixture builders (hand-built ConfigNodes shaped like a .sfs)
        // ================================================================

        private static ConfigNode MakeScenario(string name)
        {
            var sc = new ConfigNode("SCENARIO");
            sc.AddValue("name", name);
            return sc;
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// Builds a GAME node with the requested facets. Passing a null/negative
        /// for a scalar omits that SCENARIO entirely.
        /// </summary>
        private static ConfigNode MakeGame(
            double? funds = null,
            double? sciencePool = null,
            double? rep = null)
        {
            var game = new ConfigNode("GAME");

            if (funds.HasValue)
            {
                var funding = MakeScenario("Funding");
                funding.AddValue("funds", R(funds.Value));
                game.AddNode(funding);
            }

            if (sciencePool.HasValue)
            {
                var rnd = MakeScenario("ResearchAndDevelopment");
                rnd.AddValue("sci", R(sciencePool.Value));
                game.AddNode(rnd);
            }

            if (rep.HasValue)
            {
                var reputation = MakeScenario("Reputation");
                reputation.AddValue("rep", R(rep.Value));
                game.AddNode(reputation);
            }

            return game;
        }

        private static void AddSubject(ConfigNode game, string id, double sci, double cap)
        {
            ConfigNode rnd = FindScenario(game, "ResearchAndDevelopment");
            if (rnd == null)
            {
                rnd = MakeScenario("ResearchAndDevelopment");
                game.AddNode(rnd);
            }
            var subject = new ConfigNode("Science");
            subject.AddValue("id", id);
            subject.AddValue("sci", R(sci));
            subject.AddValue("cap", R(cap));
            rnd.AddNode(subject);
        }

        private static void AddFacility(ConfigNode game, string facilityId, double lvlFraction)
        {
            ConfigNode fac = FindScenario(game, "ScenarioUpgradeableFacilities");
            if (fac == null)
            {
                fac = MakeScenario("ScenarioUpgradeableFacilities");
                game.AddNode(fac);
            }
            // Facility node named by id (e.g. "SpaceCenter/LaunchPad").
            var node = new ConfigNode(facilityId);
            node.AddValue("lvl", R(lvlFraction));
            fac.AddNode(node);
        }

        private static void AddContract(ConfigNode game, string guid, string state)
        {
            ConfigNode cs = FindScenario(game, "ContractSystem");
            if (cs == null)
            {
                cs = MakeScenario("ContractSystem");
                game.AddNode(cs);
            }
            ConfigNode contracts = cs.GetNode("CONTRACTS");
            if (contracts == null)
            {
                contracts = new ConfigNode("CONTRACTS");
                cs.AddNode(contracts);
            }
            var contract = new ConfigNode("CONTRACT");
            contract.AddValue("guid", guid);
            contract.AddValue("state", state);
            contracts.AddNode(contract);
        }

        private static ConfigNode EnsureProgressNode(ConfigNode game)
        {
            ConfigNode pt = FindScenario(game, "ProgressTracking");
            if (pt == null)
            {
                pt = MakeScenario("ProgressTracking");
                game.AddNode(pt);
            }
            ConfigNode progress = pt.GetNode("Progress");
            if (progress == null)
            {
                progress = new ConfigNode("Progress");
                pt.AddNode(progress);
            }
            return progress;
        }

        /// <summary>Adds a top-level milestone node. completed=true adds the `completed` field.</summary>
        private static void AddMilestone(ConfigNode game, string id, bool completed, bool reached = true)
        {
            ConfigNode progress = EnsureProgressNode(game);
            var node = new ConfigNode(id);
            if (reached)
                node.AddValue("reached", R(12345.0));
            if (completed)
                node.AddValue("completed", R(23456.0));
            progress.AddNode(node);
        }

        /// <summary>Adds a milestone under a body subtree container (e.g. Mun/Landing).</summary>
        private static void AddBodyMilestone(ConfigNode game, string body, string child, bool completed)
        {
            ConfigNode progress = EnsureProgressNode(game);
            ConfigNode bodyNode = progress.GetNode(body);
            if (bodyNode == null)
            {
                bodyNode = new ConfigNode(body);
                progress.AddNode(bodyNode);
            }
            var node = new ConfigNode(child);
            node.AddValue("reached", R(11111.0));
            if (completed)
                node.AddValue("completed", R(22222.0));
            bodyNode.AddNode(node);
        }

        private static ConfigNode EnsureFlightState(ConfigNode game)
        {
            ConfigNode fs = game.GetNode("FLIGHTSTATE");
            if (fs == null)
            {
                fs = new ConfigNode("FLIGHTSTATE");
                game.AddNode(fs);
            }
            return fs;
        }

        private static ConfigNode AddVessel(
            ConfigNode game, string pid, uint persistentId, string name, string type = "Ship")
        {
            ConfigNode fs = EnsureFlightState(game);
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("pid", pid);
            vessel.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            vessel.AddValue("name", name);
            vessel.AddValue("type", type);
            fs.AddNode(vessel);
            return vessel;
        }

        private static void AddPartResource(ConfigNode vessel, string resName, double amount)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "testPart");
            var res = new ConfigNode("RESOURCE");
            res.AddValue("name", resName);
            res.AddValue("amount", R(amount));
            res.AddValue("maxAmount", R(amount));
            part.AddNode(res);
            vessel.AddNode(part);
        }

        private static ConfigNode FindScenario(ConfigNode game, string name)
        {
            foreach (var sc in game.GetNodes("SCENARIO"))
            {
                if (string.Equals(sc.GetValue("name"), name, StringComparison.Ordinal))
                    return sc;
            }
            return null;
        }

        // ================================================================
        // Parser tests
        // ================================================================

        [Fact]
        public void Parse_FundsScienceRep_ReadsScalars()
        {
            // Guards: a scalar key/path misread.
            var game = MakeGame(funds: 25000.5, sciencePool: 142.7, rep: -33.25);

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.Parsed);
            Assert.True(snap.HasFunds);
            Assert.Equal(25000.5, snap.Funds);
            Assert.True(snap.HasScience);
            Assert.Equal(142.7, snap.SciencePool);
            Assert.True(snap.HasRep);
            Assert.Equal(-33.25, snap.Reputation);
            Assert.Contains(logLines, l => l.Contains("[LedgerGroundTruth]") && l.Contains("ParseFunds"));
        }

        [Fact]
        public void Parse_GameWrapperAndBareRoot_BothWork()
        {
            // Guards: root-vs-GAME descent regression.
            // Bare-root form: a node whose children are FLIGHTSTATE/SCENARIO.
            var bareRoot = MakeGame(funds: 1000.0);
            AddVessel(bareRoot, "guid-a", 100u, "BareVessel");
            // Rename so it is NOT a GAME wrapper; the parser must use it directly
            // because it already has FLIGHTSTATE.
            var bare = new ConfigNode("ROOT");
            foreach (var sc in bareRoot.GetNodes("SCENARIO")) bare.AddNode(sc);
            bare.AddNode(bareRoot.GetNode("FLIGHTSTATE"));

            var bareSnap = CareerSaveParser.Parse(bare);
            Assert.True(bareSnap.Parsed);
            Assert.True(bareSnap.HasFunds);
            Assert.Equal(1000.0, bareSnap.Funds);
            Assert.Single(bareSnap.Vessels);

            // GAME-wrapped form: outer node with no FLIGHTSTATE; a GAME child holds it.
            var inner = MakeGame(funds: 2000.0);
            AddVessel(inner, "guid-b", 200u, "WrappedVessel");
            var wrapper = new ConfigNode("OUTER");
            wrapper.AddNode(inner); // inner is named "GAME"

            var wrappedSnap = CareerSaveParser.Parse(wrapper);
            Assert.True(wrappedSnap.Parsed);
            Assert.True(wrappedSnap.HasFunds);
            Assert.Equal(2000.0, wrappedSnap.Funds);
            Assert.Single(wrappedSnap.Vessels);
            Assert.Contains(logLines, l => l.Contains("descended into GAME wrapper"));
        }

        [Fact]
        public void Parse_PerSubjectScience_BuildsDict()
        {
            // Guards: Science{} child enumeration breakage.
            var game = MakeGame(sciencePool: 100.0);
            AddSubject(game, "experimentA@KerbinLanded", 5.0, 10.0);
            AddSubject(game, "experimentB@MunSpace", 8.5, 12.0);

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.HasScience);
            Assert.Equal(2, snap.SubjectScience.Count);
            Assert.Equal(5.0, snap.SubjectScience["experimentA@KerbinLanded"]);
            Assert.Equal(8.5, snap.SubjectScience["experimentB@MunSpace"]);
        }

        [Fact]
        public void Parse_FacilityFractions_ReadAllTen()
        {
            // Guards: facility node naming ("SpaceCenter/X") breakage; all ten read.
            var game = MakeGame(funds: 1.0);
            string[] ids =
            {
                "SpaceCenter/LaunchPad", "SpaceCenter/Runway", "SpaceCenter/VehicleAssemblyBuilding",
                "SpaceCenter/SpaceplaneHangar", "SpaceCenter/TrackingStation", "SpaceCenter/AstronautComplex",
                "SpaceCenter/MissionControl", "SpaceCenter/Administration", "SpaceCenter/ResearchAndDevelopment",
                "SpaceCenter/FlagPole"
            };
            double[] fracs = { 0.0, 0.5, 1.0, 0.5, 1.0, 0.0, 0.5, 1.0, 0.5, 1.0 };
            for (int i = 0; i < ids.Length; i++)
                AddFacility(game, ids[i], fracs[i]);

            var snap = CareerSaveParser.Parse(game);

            Assert.Equal(10, snap.FacilityLevelFrac.Count);
            for (int i = 0; i < ids.Length; i++)
                Assert.Equal(fracs[i], snap.FacilityLevelFrac[ids[i]]);
        }

        [Fact]
        public void Parse_ActiveVsNonActiveContracts_SeparatesStates()
        {
            // Guards: contract state filtering breakage.
            var game = MakeGame(funds: 1.0);
            AddContract(game, "guid-active-1", "Active");
            AddContract(game, "guid-active-2", "Active");
            AddContract(game, "guid-offered", "Offered");
            AddContract(game, "guid-completed", "Completed");

            var snap = CareerSaveParser.Parse(game);

            Assert.Equal(4, snap.ContractGuidsAllStates.Count);
            Assert.Equal(2, snap.ActiveContractGuids.Count);
            Assert.Contains("guid-active-1", snap.ActiveContractGuids);
            Assert.Contains("guid-active-2", snap.ActiveContractGuids);
            Assert.DoesNotContain("guid-offered", snap.ActiveContractGuids);
            Assert.DoesNotContain("guid-completed", snap.ActiveContractGuids);
            Assert.Contains("guid-offered", snap.ContractGuidsAllStates);
        }

        [Fact]
        public void Parse_Milestones_CompletedVsReached()
        {
            // Guards: completed/reached distinction loss. A reached-only node
            // (e.g. RecordsDepth) is NOT completed.
            var game = MakeGame(funds: 1.0);
            AddMilestone(game, "FirstLaunch", completed: true);
            AddMilestone(game, "RecordsDepth", completed: false, reached: true);

            var snap = CareerSaveParser.Parse(game);

            Assert.Contains("FirstLaunch", snap.AllMilestoneIds);
            Assert.Contains("RecordsDepth", snap.AllMilestoneIds);
            Assert.Contains("FirstLaunch", snap.CompletedMilestoneIds);
            Assert.DoesNotContain("RecordsDepth", snap.CompletedMilestoneIds);
        }

        [Fact]
        public void Parse_BodySubtreeMilestones_BuildsQualifiedIds()
        {
            // Guards: nested body subtrees not walked into qualified ids.
            var game = MakeGame(funds: 1.0);
            AddBodyMilestone(game, "Mun", "Landing", completed: true);
            AddBodyMilestone(game, "Mun", "Flyby", completed: true);

            var snap = CareerSaveParser.Parse(game);

            // Both qualified and bare ids emitted.
            Assert.Contains("Mun/Landing", snap.AllMilestoneIds);
            Assert.Contains("Landing", snap.AllMilestoneIds);
            Assert.Contains("Mun/Landing", snap.CompletedMilestoneIds);
            Assert.Contains("Landing", snap.CompletedMilestoneIds);
            Assert.Contains("Mun/Flyby", snap.AllMilestoneIds);
        }

        [Fact]
        public void Parse_VesselResourceTotals_SumsAcrossParts()
        {
            // Guards: per-part RESOURCE summation breakage.
            var game = MakeGame(funds: 1.0);
            var vessel = AddVessel(game, "guid-v", 500u, "Tanker");
            AddPartResource(vessel, "LiquidFuel", 400.0);
            AddPartResource(vessel, "LiquidFuel", 200.0);
            AddPartResource(vessel, "Oxidizer", 488.0);

            var snap = CareerSaveParser.Parse(game);

            Assert.Single(snap.Vessels);
            var sv = snap.Vessels[0];
            Assert.Equal("guid-v", sv.Pid);
            Assert.Equal(500u, sv.PersistentId);
            Assert.Equal("Tanker", sv.Name);
            Assert.Equal(600.0, sv.ResourceTotals["LiquidFuel"]);
            Assert.Equal(488.0, sv.ResourceTotals["Oxidizer"]);
        }

        [Fact]
        public void Parse_MissingScenario_SetsHasFalseNoThrow()
        {
            // Guards: an absent SCENARIO throwing. Only a FLIGHTSTATE present.
            var game = new ConfigNode("GAME");
            AddVessel(game, "guid-only", 1u, "OnlyVessel");

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.Parsed); // FLIGHTSTATE makes it recognizable
            Assert.False(snap.HasFunds);
            Assert.False(snap.HasScience);
            Assert.False(snap.HasRep);
            Assert.Empty(snap.SubjectScience);
            Assert.Empty(snap.FacilityLevelFrac);
            Assert.Empty(snap.ActiveContractGuids);
            Assert.Empty(snap.CompletedMilestoneIds);
            Assert.Single(snap.Vessels);
        }

        [Fact]
        public void Parse_UnrecognizableShape_ParsedFalseNoThrow()
        {
            // Guards: no GAME/FLIGHTSTATE/SCENARIO -> Parsed=false with reason, never throws.
            var junk = new ConfigNode("SOMETHING");
            junk.AddValue("foo", "bar");

            var snap = CareerSaveParser.Parse(junk);

            Assert.False(snap.Parsed);
            Assert.False(string.IsNullOrEmpty(snap.Reason));
        }

        [Fact]
        public void Parse_NullRoot_ParsedFalseNoThrow()
        {
            var snap = CareerSaveParser.Parse(null);

            Assert.False(snap.Parsed);
            Assert.Contains("null", snap.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Parse_CommaLocale_InvariantCulture()
        {
            // Guards: culture leaking into double parsing. The fixture writes
            // values with InvariantCulture ("."); we force the thread to a
            // comma-decimal locale during the parse and confirm the value round
            // trips. The helper must already use InvariantCulture internally.
            var game = MakeGame(funds: 12345.67, sciencePool: 89.01);

            var prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // comma decimal
                var snap = CareerSaveParser.Parse(game);

                Assert.True(snap.HasFunds);
                Assert.Equal(12345.67, snap.Funds);
                Assert.True(snap.HasScience);
                Assert.Equal(89.01, snap.SciencePool);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
