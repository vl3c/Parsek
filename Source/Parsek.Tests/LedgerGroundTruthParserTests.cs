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

        /// <summary>
        /// Adds a KERBAL under GAME &gt; ROSTER (a DIRECT GAME child, NOT a SCENARIO).
        /// A null name omits the `name` value entirely (the skip path).
        /// </summary>
        private static void AddKerbal(
            ConfigNode game, string name, string type, string trait, string state,
            string gender = "Male")
        {
            ConfigNode roster = game.GetNode("ROSTER");
            if (roster == null)
            {
                roster = new ConfigNode("ROSTER");
                game.AddNode(roster);
            }
            var k = new ConfigNode("KERBAL");
            if (name != null)
                k.AddValue("name", name);
            k.AddValue("gender", gender);
            if (type != null) k.AddValue("type", type);
            if (trait != null) k.AddValue("trait", trait);
            if (state != null) k.AddValue("state", state);
            roster.AddNode(k);
        }

        /// <summary>
        /// Adds a Tech node inside the ResearchAndDevelopment SCENARIO. Purchases are
        /// the REPEATED `part` values, exactly as KSP writes them. A null id omits
        /// the `id` value (the skip path).
        /// </summary>
        private static void AddTech(ConfigNode game, string id, double cost, params string[] parts)
        {
            ConfigNode rnd = FindScenario(game, "ResearchAndDevelopment");
            if (rnd == null)
            {
                rnd = MakeScenario("ResearchAndDevelopment");
                game.AddNode(rnd);
            }
            var tech = new ConfigNode("Tech");
            if (id != null)
                tech.AddValue("id", id);
            tech.AddValue("state", "Available");
            tech.AddValue("cost", R(cost));
            foreach (string p in parts ?? new string[0])
                tech.AddValue("part", p);
            rnd.AddNode(tech);
        }

        /// <summary>Ensures the StrategySystem SCENARIO + its STRATEGIES block exist.</summary>
        private static ConfigNode EnsureStrategiesNode(ConfigNode game)
        {
            ConfigNode ss = FindScenario(game, "StrategySystem");
            if (ss == null)
            {
                ss = MakeScenario("StrategySystem");
                game.AddNode(ss);
            }
            ConfigNode strategies = ss.GetNode("STRATEGIES");
            if (strategies == null)
            {
                strategies = new ConfigNode("STRATEGIES");
                ss.AddNode(strategies);
            }
            return strategies;
        }

        /// <summary>
        /// Adds a STRATEGY node in the REAL stock shape (name / date / factor +
        /// EFFECT children; stock writes NO isActive field). Passing
        /// <paramref name="explicitIsActive"/> writes the defensive isActive value.
        /// </summary>
        private static void AddStrategy(
            ConfigNode game, string name, double date, double factor,
            bool? explicitIsActive = null)
        {
            ConfigNode strategies = EnsureStrategiesNode(game);
            var st = new ConfigNode("STRATEGY");
            if (name != null)
                st.AddValue("name", name);
            st.AddValue("date", R(date));
            st.AddValue("factor", R(factor));
            if (explicitIsActive.HasValue)
                st.AddValue("isActive", explicitIsActive.Value ? "True" : "False");
            var effect = new ConfigNode("EFFECT");
            effect.AddValue("name", "CurrencyConverter");
            st.AddNode(effect);
            strategies.AddNode(st);
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

        // ================================================================
        // A.2 - ROSTER (a DIRECT GAME child, not a SCENARIO)
        // ================================================================

        [Fact]
        public void Parse_Roster_ReadsEveryKerbalField()
        {
            // Guards: the ROSTER path (FindScenario can NEVER reach it) and the
            // per-kerbal field set.
            var game = MakeGame(funds: 1.0);
            AddKerbal(game, "Jebediah Kerman", "Crew", "Pilot", "Available");
            AddKerbal(game, "Bill Kerman", "Crew", "Engineer", "Assigned");
            AddKerbal(game, "Valentina Kerman", "Applicant", "Pilot", "Available", gender: "Female");

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.HasRoster);
            Assert.Equal(3, snap.Roster.Count);

            var jeb = snap.Roster[0];
            Assert.Equal("Jebediah Kerman", jeb.Name);
            Assert.Equal("Male", jeb.Gender);
            Assert.Equal("Crew", jeb.Type);
            Assert.Equal("Pilot", jeb.Trait);
            Assert.Equal("Available", jeb.State);

            Assert.Equal("Assigned", snap.Roster[1].State);
            Assert.Equal("Female", snap.Roster[2].Gender);
            Assert.Equal("Applicant", snap.Roster[2].Type);

            Assert.Contains(logLines, l => l.Contains("[LedgerGroundTruth]")
                && l.Contains("ParseRoster: kerbals=3") && l.Contains("crew=2") && l.Contains("applicants=1"));
        }

        [Fact]
        public void Parse_Roster_SkipsNamelessKerbalAndCountsDead()
        {
            // Guards: a KERBAL with no `name` has no identity to key on and must be
            // skipped rather than added as a blank entry / throwing.
            var game = MakeGame(funds: 1.0);
            AddKerbal(game, "Bob Kerman", "Crew", "Scientist", "Dead");
            AddKerbal(game, null, "Crew", "Pilot", "Available");

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.HasRoster);
            Assert.Single(snap.Roster);
            Assert.Equal("Bob Kerman", snap.Roster[0].Name);
            Assert.Contains(logLines, l => l.Contains("ParseRoster")
                && l.Contains("dead=1") && l.Contains("skippedNoName=1"));
        }

        [Fact]
        public void Parse_NoRosterNode_HasRosterFalseNoThrow()
        {
            // Guards: an absent ROSTER throwing / claiming a roster facet.
            var game = MakeGame(funds: 1.0);

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.Parsed);
            Assert.False(snap.HasRoster);
            Assert.Empty(snap.Roster);
            Assert.Contains(logLines, l => l.Contains("ParseRoster: no ROSTER node"));
        }

        [Fact]
        public void Parse_EmptyRosterNode_HasRosterTrueZeroKerbals()
        {
            // Guards: a present-but-empty ROSTER must report the facet as PRESENT
            // (an empty roster is a real state), not collapse to "no roster".
            var game = MakeGame(funds: 1.0);
            game.AddNode(new ConfigNode("ROSTER"));

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.HasRoster);
            Assert.Empty(snap.Roster);
        }

        // ================================================================
        // A.3 - tech unlock set + part purchases (inside the R&D SCENARIO)
        // ================================================================

        [Fact]
        public void Parse_TechNodes_UnlockSetAndRepeatedPartValues()
        {
            // Guards: the Tech child enumeration and the REPEATED `part` value read
            // (a single GetValue would keep only the first part per node).
            var game = MakeGame(sciencePool: 100.0);
            AddTech(game, "start", 0.0, "mk1pod.v2", "basicFin", "parachuteSingle");
            AddTech(game, "basicRocketry", 5.0, "liquidEngine2", "fuelTankSmallFlat");

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.HasTechTree);
            Assert.Equal(2, snap.UnlockedTechIds.Count);
            Assert.Contains("start", snap.UnlockedTechIds);
            Assert.Contains("basicRocketry", snap.UnlockedTechIds);

            Assert.Equal(5, snap.PurchasedPartNames.Count);
            Assert.Contains("mk1pod.v2", snap.PurchasedPartNames);
            Assert.Contains("fuelTankSmallFlat", snap.PurchasedPartNames);

            Assert.Equal(3, snap.TechNodePartCounts["start"]);
            Assert.Equal(2, snap.TechNodePartCounts["basicRocketry"]);
        }

        [Fact]
        public void Parse_TechNode_WithoutId_IsSkipped()
        {
            // Guards: an id-less Tech node has no identity and must not enter the
            // unlock set (its parts are not attributable either).
            var game = MakeGame(sciencePool: 10.0);
            AddTech(game, null, 0.0, "orphanPart");
            AddTech(game, "stability", 18.0, "winglet");

            var snap = CareerSaveParser.Parse(game);

            Assert.Single(snap.UnlockedTechIds);
            Assert.Contains("stability", snap.UnlockedTechIds);
            Assert.Single(snap.PurchasedPartNames);
            Assert.DoesNotContain("orphanPart", snap.PurchasedPartNames);
            Assert.Contains(logLines, l => l.Contains("ParseTechTree") && l.Contains("skippedNoId=1"));
        }

        [Fact]
        public void Parse_TechTreeFacet_IsIndependentOfSciencePoolParse()
        {
            // Guards: coupling HasTechTree to HasScience. An R&D SCENARIO with no
            // parsable `sci` still carries a real unlock set.
            var game = new ConfigNode("GAME");
            var rnd = MakeScenario("ResearchAndDevelopment");
            game.AddNode(rnd);
            AddTech(game, "start", 0.0, "mk1pod.v2");

            var snap = CareerSaveParser.Parse(game);

            Assert.False(snap.HasScience);
            Assert.True(snap.HasTechTree);
            Assert.Contains("start", snap.UnlockedTechIds);
        }

        [Fact]
        public void Parse_NoResearchAndDevelopment_NoTechFacet()
        {
            var game = MakeGame(funds: 1.0);

            var snap = CareerSaveParser.Parse(game);

            Assert.False(snap.HasTechTree);
            Assert.Empty(snap.UnlockedTechIds);
            Assert.Empty(snap.PurchasedPartNames);
            Assert.Empty(snap.TechNodePartCounts);
        }

        // ================================================================
        // A.4 - StrategySystem (SHAPE-ONLY)
        // ================================================================

        [Fact]
        public void Parse_Strategies_PresenceIsTheActiveSignal()
        {
            // Guards: the real stock STRATEGY shape (name / date / factor; NO
            // isActive field). Presence in STRATEGIES means active.
            var game = MakeGame(funds: 1.0);
            AddStrategy(game, "PatentsLicensingCfg", 17022.76, 0.05);

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.HasStrategySystem);
            Assert.Single(snap.Strategies);
            var st = snap.Strategies[0];
            Assert.Equal("PatentsLicensingCfg", st.Name);
            Assert.True(st.IsActive);
            Assert.Equal(17022.76, st.ActivatedUT);
            Assert.Equal(0.05, st.Factor);
            Assert.Contains("PatentsLicensingCfg", snap.ActiveStrategyIds);
        }

        [Fact]
        public void Parse_Strategies_ExplicitIsActiveFalseIsHonoured()
        {
            // Guards: the defensive isActive read. Stock never writes it, but if a
            // producer ever does, "False" must NOT land in the active set.
            var game = MakeGame(funds: 1.0);
            AddStrategy(game, "ResearchTiming", 100.0, 0.25, explicitIsActive: false);
            AddStrategy(game, "PatentsLicensingCfg", 200.0, 0.5, explicitIsActive: true);

            var snap = CareerSaveParser.Parse(game);

            Assert.Equal(2, snap.Strategies.Count);
            Assert.Single(snap.ActiveStrategyIds);
            Assert.Contains("PatentsLicensingCfg", snap.ActiveStrategyIds);
            Assert.DoesNotContain("ResearchTiming", snap.ActiveStrategyIds);
        }

        [Fact]
        public void Parse_EmptyStrategiesBlock_IsTheNormalShape()
        {
            // Guards: the EMPTY STRATEGIES block (every committed fixture is shaped
            // that way - a deactivated strategy is REMOVED from the save). It must
            // report the SCENARIO as present with zero strategies, never a failure.
            var game = MakeGame(funds: 1.0);
            EnsureStrategiesNode(game);

            var snap = CareerSaveParser.Parse(game);

            Assert.True(snap.Parsed);
            Assert.True(snap.HasStrategySystem);
            Assert.Empty(snap.Strategies);
            Assert.Empty(snap.ActiveStrategyIds);
            Assert.Contains(logLines, l => l.Contains("ParseStrategies")
                && l.Contains("STRATEGIES block is empty"));
        }

        [Fact]
        public void Parse_NoStrategySystemScenario_HasStrategySystemFalse()
        {
            var game = MakeGame(funds: 1.0);

            var snap = CareerSaveParser.Parse(game);

            Assert.False(snap.HasStrategySystem);
            Assert.Empty(snap.Strategies);
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
