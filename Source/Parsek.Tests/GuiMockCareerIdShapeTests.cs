using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Career catalogue's synthetic ids must be ids the GAME can hold, because the
    /// window's title cells are resolved FROM them: a strategy title comes from
    /// <c>StrategySystem.Instance</c> keyed by the cfg name (falling back to the raw id),
    /// and a milestone title is <c>SpaceBeforeCapitals</c> over a stock ProgressNode key.
    ///
    /// <para>An invented id therefore renders a title no career can produce - which is the
    /// same class of defect as a typed cell, one layer up. The first version of this
    /// catalogue had four invented strategy names (including
    /// <c>"AggressiveNegotiations"</c> for a strategy stock spells
    /// <c>"AgressiveNegotiations"</c>, and which is a <c>CurrencyOperation</c> with no
    /// Source -&gt; Target flow at all) and two milestone ids in neither production
    /// shape.</para>
    ///
    /// <para><b>The stock cfg names are pinned as TEST DATA, and that is deliberate.</b>
    /// They are data rather than window text - the cfg files are not in the repo and are
    /// not readable on a CI runner - so the honest gate is a pinned list here plus a
    /// cross-check against the committed FIXTURES' own ledgers, which were written by a
    /// real KSP.</para>
    /// </summary>
    public class GuiMockCareerIdShapeTests
    {
        /// <summary>
        /// The stock strategy cfg names, from
        /// <c>GameData/Squad/Strategies/Strategies.cfg</c> (KSP 1.12.5), with each one's
        /// module kind. Only a <c>CurrencyConverter</c> declares an input / output pair, so
        /// only a converter can produce the window's <c>Source -&gt; Target @ pct%</c> Flow
        /// cell - which is why the kind is pinned beside the name.
        /// </summary>
        private static readonly Dictionary<string, string> StockStrategyKinds =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "AppreciationCampaignCfg", "CurrencyConverter" },
                { "FundraisingCampaignCfg", "CurrencyConverter" },
                { "OpenSourceTechProgramCfg", "CurrencyConverter" },
                { "UnpaidResearchProgramCfg", "CurrencyConverter" },
                { "OutsourcedResearchCfg", "CurrencyConverter" },
                { "PatentsLicensingCfg", "CurrencyConverter" },
                // Note the stock spelling: one 'g'. It is also an OPERATION, not a
                // converter, so a mocked Flow cell for it would be fiction.
                { "AgressiveNegotiations", "CurrencyOperation" },
                { "LeadershipInitiative", "CurrencyOperation" },
                { "RecoveryTransponders", "ValueModifier" },
                { "BailoutGrant", "CurrencyExchanger" },
                { "researchIPsellout", "CurrencyExchanger" },
            };

        /// <summary>
        /// Each stock CONVERTER's declared input -&gt; output, so a mocked Activate cannot
        /// invent a flow the strategy does not have.
        /// </summary>
        private static readonly Dictionary<string, string> StockConverterFlows =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "AppreciationCampaignCfg", "Funds->Reputation" },
                { "FundraisingCampaignCfg", "Reputation->Funds" },
                { "OpenSourceTechProgramCfg", "Science->Reputation" },
                { "UnpaidResearchProgramCfg", "Reputation->Science" },
                { "OutsourcedResearchCfg", "Funds->Science" },
                { "PatentsLicensingCfg", "Science->Funds" },
            };

        [Fact]
        public void EveryMockedStrategyIsAStockConverterWithItsOwnDeclaredFlow()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.CareerWindow))
            {
                CareerStateWindowUI.StrategiesTabVM tab =
                    state.Build().Career.Value.Strategies;
                foreach (CareerStateWindowUI.StrategyRow row in
                         tab.CurrentRows.Concat(tab.ProjectedRows))
                {
                    seen.Add(row.StrategyId);
                    Assert.True(StockStrategyKinds.ContainsKey(row.StrategyId),
                        "state '" + state.Id + "' activates strategy '" + row.StrategyId
                        + "', which is not a stock cfg name - so the window's Title cell "
                        + "cannot draw the display title a real one would. Stock names: "
                        + string.Join(", ", StockStrategyKinds.Keys.OrderBy(k => k)));
                    Assert.Equal("CurrencyConverter", StockStrategyKinds[row.StrategyId]);

                    string flow = row.SourceResource + "->" + row.TargetResource;
                    Assert.Equal(StockConverterFlows[row.StrategyId], flow);
                }
            }
            Assert.True(seen.Count >= 4,
                "only " + seen.Count + " distinct strategy id(s) across the Career states; "
                + "this gate is close to vacuous");
        }

        [Fact]
        public void EveryMockedMilestoneIdIsAProductionShapedProgressNodeKey()
        {
            // Two production shapes and nothing else: a bare node name ("FirstLaunch") or
            // "<Body>/<Node>" ("Mun/Landing"). Both are all over the committed fixtures'
            // ledgers; "FirstOrbitKerbin" is neither, which is what the first version
            // shipped.
            var shape = new Regex(@"^[A-Za-z][A-Za-z0-9]*(/[A-Za-z][A-Za-z0-9]*)?$",
                                  RegexOptions.CultureInvariant);
            HashSet<string> fixtureIds = MilestoneIdsInCommittedFixtures();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.CareerWindow))
            {
                foreach (CareerStateWindowUI.MilestoneRow row in
                         state.Build().Career.Value.Milestones.Rows)
                {
                    seen.Add(row.MilestoneId);
                    Assert.True(shape.IsMatch(row.MilestoneId),
                        "state '" + state.Id + "' credits milestone '" + row.MilestoneId
                        + "', which is neither a bare ProgressNode name nor "
                        + "<Body>/<Node> - the two shapes production writes");
                }
            }
            Assert.True(seen.Count >= 4,
                "only " + seen.Count + " distinct milestone id(s); this gate is close to "
                + "vacuous");

            // And the cross-check that makes the shape claim more than a regex: at least
            // one mocked id is an id a REAL KSP wrote into a committed fixture's ledger.
            Assert.True(fixtureIds.Count > 0,
                "no milestoneId found in any committed fixture ledger; this cross-check "
                + "is vacuous (did the fixture layout move?)");
            Assert.True(seen.Overlaps(fixtureIds),
                "no mocked milestone id appears in ANY committed fixture ledger, so the "
                + "shape claim rests on a regex alone. Mocked: "
                + string.Join(", ", seen.OrderBy(k => k, StringComparer.Ordinal)));
        }

        /// <summary>Every <c>milestoneId</c> the committed fixtures' ledgers carry - ids a
        /// real KSP wrote, which is what makes them evidence rather than a convention.
        /// </summary>
        private static HashSet<string> MilestoneIdsInCommittedFixtures()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            string root = Path.Combine(ResolveRepoRoot(), "harness", "fixtures", "saves");
            if (!Directory.Exists(root)) return ids;
            var re = new Regex(@"milestoneId\s*=\s*([A-Za-z0-9/._-]+)",
                               RegexOptions.CultureInvariant);
            foreach (string path in Directory.EnumerateFiles(root, "*.pgld",
                                                             SearchOption.AllDirectories))
            {
                foreach (Match m in re.Matches(File.ReadAllText(path)))
                    ids.Add(m.Groups[1].Value);
            }
            return ids;
        }

        [Fact]
        public void EveryMockedContractAndFacilityIdIsProductionShaped()
        {
            // A contract id is a stock ContractID (a number) or a mod-generated token, and
            // the window prefers the action's own ContractTitle over any lookup - so the
            // id shape only has to be a plausible token. A FACILITY id is different: it is
            // matched against FACILITY_DISPLAY_ORDER, and an id outside that list draws no
            // row at all, which would make a facility state a picture of nothing.
            var facilityIds = new HashSet<string>(
                CareerStateWindowUI.FACILITY_DISPLAY_ORDER, StringComparer.Ordinal);
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.CareerWindow))
            {
                CareerStateWindowUI.CareerStateViewModel vm = state.Build().Career.Value;
                foreach (CareerStateWindowUI.FacilityRow row in vm.Facilities.Rows)
                {
                    Assert.True(facilityIds.Contains(row.FacilityId),
                        "state '" + state.Id + "' produced facility row '" + row.FacilityId
                        + "', which is not in FACILITY_DISPLAY_ORDER");
                }
                foreach (CareerStateWindowUI.ContractRow row in
                         vm.Contracts.CurrentRows.Concat(vm.Contracts.ProjectedRows))
                {
                    Assert.False(string.IsNullOrWhiteSpace(row.ContractId));
                    Assert.False(string.IsNullOrWhiteSpace(row.DisplayTitle),
                        "state '" + state.Id + "' contract '" + row.ContractId
                        + "' has no title, so the window would draw the raw id - the "
                        + "fallback rather than the ordinary cell");
                }
            }
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
