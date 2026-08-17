using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A.2-A.4 (career-ledger lane): <see cref="CareerSaveParser"/> against the two
    /// COMMITTED real-save fixtures, so the roster / tech / part-purchase / strategy
    /// parse paths are pinned against KSP's own bytes rather than only against
    /// hand-built ConfigNodes.
    ///
    /// Both fixtures are shape references, NOT value oracles: every number asserted
    /// here is a COUNT of nodes KSP itself wrote, so it cannot rot when a recalc bug
    /// is fixed. The synthetic-node coverage (skip paths, isActive handling, missing
    /// facets) lives in <c>LedgerGroundTruthParserTests</c>.
    ///
    /// - <c>Source/Parsek.Tests/Fixtures/C2Career/persistent.sfs</c> - the real short
    ///   career played 2026-08-17 on current code (see C2CareerLedgerReplayTests).
    /// - <c>harness/fixtures/saves/fresh-career/persistent.sfs</c> - the harness
    ///   career template the L1 specs stage; its roster is what the L1 dismiss
    ///   scenario mutates.
    /// </summary>
    [Collection("Sequential")]
    public class CareerSaveParserFixtureTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly List<string> logLines = new List<string>();

        public CareerSaveParserFixtureTests()
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

        /// <summary>
        /// The shared root resolver: walks up probing for Source/Parsek.sln rather than
        /// hard-coding how deep the xUnit output directory sits.
        /// </summary>
        private static string RepoRoot()
        {
            return SyntheticRecordingTests.ResolveProjectRoot();
        }

        private static CareerSaveSnapshot ParseSave(string path)
        {
            Assert.True(File.Exists(path), $"fixture save not found at '{path}'");
            ConfigNode root = ConfigNode.Load(path);
            Assert.NotNull(root);
            CareerSaveSnapshot snap = CareerSaveParser.Parse(root);
            Assert.True(snap.Parsed, $"CareerSaveParser rejected '{path}': {snap.Reason}");
            return snap;
        }

        private static CareerSaveSnapshot ParseC2()
        {
            return ParseSave(Path.Combine(
                RepoRoot(), "Source", "Parsek.Tests", "Fixtures", "C2Career", "persistent.sfs"));
        }

        private static CareerSaveSnapshot ParseFreshCareer()
        {
            return ParseSave(Path.Combine(
                RepoRoot(), "harness", "fixtures", "saves", "fresh-career", "persistent.sfs"));
        }

        // ================================================================
        // C2Career (real played career)
        // ================================================================

        [Fact]
        public void C2Fixture_Roster_ParsesAllFifteenKerbals()
        {
            // Guards: the GAME > ROSTER path against KSP's own bytes. 15 KERBAL
            // nodes: the stock four (Crew) plus eleven Applicants.
            var snap = ParseC2();

            Assert.True(snap.HasRoster);
            Assert.Equal(15, snap.Roster.Count);

            var byName = new Dictionary<string, SaveKerbal>(StringComparer.Ordinal);
            foreach (var k in snap.Roster)
                byName[k.Name] = k;

            Assert.True(byName.ContainsKey("Jebediah Kerman"));
            Assert.Equal("Crew", byName["Jebediah Kerman"].Type);
            Assert.Equal("Pilot", byName["Jebediah Kerman"].Trait);
            Assert.Equal("Available", byName["Jebediah Kerman"].State);
            Assert.Equal("Engineer", byName["Bill Kerman"].Trait);
            Assert.Equal("Scientist", byName["Bob Kerman"].Trait);
            Assert.Equal("Female", byName["Valentina Kerman"].Gender);
            Assert.Equal("Applicant", byName["Gregmin Kerman"].Type);

            int crew = 0;
            foreach (var k in snap.Roster)
            {
                if (string.Equals(k.Type, "Crew", StringComparison.Ordinal))
                    crew++;
            }
            Assert.Equal(4, crew);
        }

        [Fact]
        public void C2Fixture_TechNodes_FiveNodesAndFortyFivePartPurchases()
        {
            // Guards: the Tech unlock set + the repeated `part` values, measured off
            // the fixture (5 Tech nodes; 45 part values, all distinct).
            var snap = ParseC2();

            Assert.True(snap.HasTechTree);
            Assert.Equal(5, snap.UnlockedTechIds.Count);
            Assert.Contains("start", snap.UnlockedTechIds);
            Assert.Contains("basicRocketry", snap.UnlockedTechIds);
            Assert.Contains("stability", snap.UnlockedTechIds);
            Assert.Contains("engineering101", snap.UnlockedTechIds);
            Assert.Contains("survivability", snap.UnlockedTechIds);

            Assert.Equal(45, snap.PurchasedPartNames.Count);
            Assert.Contains("GooExperiment", snap.PurchasedPartNames);
            Assert.Contains("restock-engine-torch", snap.PurchasedPartNames);

            Assert.Equal(5, snap.TechNodePartCounts.Count);
            Assert.Equal(12, snap.TechNodePartCounts["start"]);
            Assert.Equal(11, snap.TechNodePartCounts["basicRocketry"]);
            Assert.Equal(11, snap.TechNodePartCounts["stability"]);
            Assert.Equal(3, snap.TechNodePartCounts["engineering101"]);
            Assert.Equal(8, snap.TechNodePartCounts["survivability"]);

            int summed = 0;
            foreach (var kvp in snap.TechNodePartCounts)
                summed += kvp.Value;
            Assert.Equal(45, summed);
        }

        [Fact]
        public void C2Fixture_StrategySystem_PresentWithAnEmptyStrategiesBlock()
        {
            // Guards: the empty-STRATEGIES shape. c2's strategy (researchIPsellout)
            // was activated AND deactivated before the save, so KSP wrote no
            // STRATEGY node - the SCENARIO is present, the block is empty, and that
            // must parse cleanly rather than reading as a missing facet or a throw.
            var snap = ParseC2();

            Assert.True(snap.HasStrategySystem);
            Assert.Empty(snap.Strategies);
            Assert.Empty(snap.ActiveStrategyIds);
            Assert.Contains(logLines, l => l.Contains("[LedgerGroundTruth]")
                && l.Contains("ParseStrategies"));
        }

        [Fact]
        public void C2Fixture_NewFacetsDoNotDisturbTheExistingOnes()
        {
            // Guards: an A.2-A.4 parse regression silently breaking a facet the
            // ground-truth harness already gates on (the pools are HARD facets).
            var snap = ParseC2();

            Assert.True(snap.HasFunds);
            Assert.True(snap.HasScience);
            Assert.Equal(641.790283, snap.SciencePool, 6);
            Assert.True(snap.HasRep);
            Assert.NotEmpty(snap.FacilityLevelFrac);
        }

        // ================================================================
        // fresh-career (the harness L1 template)
        // ================================================================

        [Fact]
        public void FreshCareerFixture_Roster_CarriesTheFiveTemplateKerbals()
        {
            // Guards: the roster the L1 dismiss scenario mutates. Bill Kerman is the
            // kerbal that spec dismisses, so his PRESENCE in the template is the
            // precondition of the produced-save roster assertion.
            var snap = ParseFreshCareer();

            Assert.True(snap.HasRoster);
            Assert.Equal(5, snap.Roster.Count);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in snap.Roster)
                names.Add(k.Name);

            Assert.Contains("Jebediah Kerman", names);
            Assert.Contains("Bill Kerman", names);
            Assert.Contains("Bob Kerman", names);
            Assert.Contains("Valentina Kerman", names);
            Assert.Contains("Verhat Kerman", names);
        }

        [Fact]
        public void FreshCareerFixture_TechTree_OneStartNodeWithElevenParts()
        {
            var snap = ParseFreshCareer();

            Assert.True(snap.HasTechTree);
            Assert.Single(snap.UnlockedTechIds);
            Assert.Contains("start", snap.UnlockedTechIds);
            Assert.Equal(11, snap.PurchasedPartNames.Count);
            Assert.Equal(11, snap.TechNodePartCounts["start"]);
            Assert.Contains("mk1pod.v2", snap.PurchasedPartNames);
        }
    }
}
