using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Career catalogue's synthetic ids must be ids the GAME can hold, because the
    /// window's title cells are resolved FROM them: a strategy title comes from
    /// <c>StrategySystem.Instance</c> keyed by the cfg name (falling back to the raw id).
    ///
    /// <para>An invented id therefore renders a title no career can produce - which is the
    /// same class of defect as a typed cell, one layer up. The first version of this
    /// catalogue had four invented strategy names (including
    /// <c>"AggressiveNegotiations"</c> for a strategy stock spells
    /// <c>"AgressiveNegotiations"</c>, and which is a <c>CurrencyOperation</c> with no
    /// Source -&gt; Target flow at all).</para>
    ///
    /// <para><b>The stock cfg names are pinned as TEST DATA, and that is deliberate.</b>
    /// They are data rather than window text - the cfg files are not in the repo and are
    /// not readable on a CI runner - so the honest gate is a pinned list here.</para>
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
        public void EveryMockedContractIdIsProductionShaped()
        {
            // A contract id is a stock ContractID (a number) or a mod-generated token, and
            // the window prefers the action's own ContractTitle over any lookup - so the
            // id shape only has to be a plausible token, and the title must be present.
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.CareerWindow))
            {
                CareerStateWindowUI.CareerStateViewModel vm = state.Build().Career.Value;
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
    }
}
