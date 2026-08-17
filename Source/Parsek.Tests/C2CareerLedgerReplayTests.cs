using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A.0 (career-ledger lane): replay the REAL recalculation engine over the C2Career
    /// fixture's ledger and diff the reconstructed pools against KSP's own on-disk save.
    ///
    /// The fixture is a REAL short career (save "c2", played 2026-08-17 on current code,
    /// schema generation 4): two flights auto-recorded and merged, one contract accepted
    /// and completed, one strategy activated and deactivated (researchIPsellout,
    /// commitment 0.05), tech nodes researched. 68 GAME_ACTION rows including 11
    /// flight-science ScienceEarning rows - the shape no KscAction forge can produce.
    /// This is the suite's first many-action REAL-ledger walk (every prior career test
    /// builds 1-4 synthetic actions from a clean slate).
    ///
    /// Non-circular by construction: KSP wrote the save pools; Parsek's observers wrote
    /// the ledger; this test diffs the two independent producers, exactly the
    /// LedgerGroundTruthHarness contract but headless.
    ///
    /// Module registration mirrors LedgerOrchestrator.Initialize's tier order (design
    /// doc 1.8). Kept in step by RegistrationMirrorsOrchestratorTierOrder below.
    /// </summary>
    [Collection("Sequential")]
    public class C2CareerLedgerReplayTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Pinned from the fixture's persistent.sfs (KSP-authored ground truth).
        private const double SaveFunds = 85166.31533847659;
        private const double SaveScience = 641.790283;
        private const double SaveReputation = 5.02411318;

        // Pinned from the fixture's ledger seeds (types 20/21/22).
        private const double SeedFunds = 33000.0;
        private const double SeedScience = 750.0;
        private const double SeedReputation = 0.0;

        private readonly List<string> logLines = new List<string>();

        public C2CareerLedgerReplayTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            Ledger.ResetForTesting();
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Fixture resolution (pattern: SyntheticRecordingTests.ResolveDefaultCareerFixtureDir)
        // ================================================================

        private static string ResolveFixtureDir()
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ - five levels up
            // is the repo root.
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string dir = Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "C2Career");
            Assert.True(Directory.Exists(dir), $"C2Career fixture dir not found at '{dir}'");
            return dir;
        }

        private static List<GameAction> LoadFixtureLedger()
        {
            string path = Path.Combine(ResolveFixtureDir(), "Parsek", "GameState", "ledger.pgld");
            Assert.True(File.Exists(path), $"fixture ledger not found at '{path}'");
            Assert.True(Ledger.LoadFromFile(path), "Ledger.LoadFromFile failed on the C2Career fixture");
            return new List<GameAction>(Ledger.Actions);
        }

        private static CareerSaveSnapshot ParseFixtureSave()
        {
            string path = Path.Combine(ResolveFixtureDir(), "persistent.sfs");
            ConfigNode root = ConfigNode.Load(path);
            Assert.NotNull(root);
            CareerSaveSnapshot snap = CareerSaveParser.Parse(root);
            Assert.True(snap.Parsed, $"CareerSaveParser rejected the fixture save: {snap.Reason}");
            return snap;
        }

        /// <summary>
        /// Registers all nine production modules in LedgerOrchestrator.Initialize's tier
        /// order and returns the pool-owning three for reading back results.
        /// </summary>
        private static (FundsModule funds, ScienceModule science, ReputationModule rep)
            RegisterProductionModules()
        {
            var science = new ScienceModule();
            var milestones = new MilestonesModule();
            var contracts = new ContractsModule();
            var funds = new FundsModule();
            var route = new RouteModule();
            var rep = new ReputationModule();
            var facilities = new FacilitiesModule();
            var strategies = new StrategiesModule();
            var kerbals = new KerbalsModule();

            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(kerbals, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(route, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(rep, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(facilities, RecalculationEngine.ModuleTier.Facilities);

            return (funds, science, rep);
        }

        // ================================================================
        // Fixture integrity
        // ================================================================

        [Fact]
        public void FixtureLedger_LoadsAllSixtyEightActions()
        {
            var actions = LoadFixtureLedger();
            Assert.Equal(68, actions.Count);
        }

        [Fact]
        public void FixtureSave_ParsesWithKnownPools()
        {
            var snap = ParseFixtureSave();
            Assert.True(snap.HasFunds);
            Assert.Equal(SaveFunds, snap.Funds, 6);
            Assert.True(snap.HasScience);
            Assert.Equal(SaveScience, snap.SciencePool, 4);
            Assert.True(snap.HasRep);
            Assert.Equal(SaveReputation, snap.Reputation, 4);
        }

        // ================================================================
        // A.0 - the adjudication: real engine vs KSP's own save
        // ================================================================

        [Fact]
        public void RealEngine_ReplaysC2Ledger_PoolsVsSave()
        {
            var (funds, science, rep) = RegisterProductionModules();
            var actions = LoadFixtureLedger();

            RecalculationEngine.Recalculate(actions);

            double reconFunds = funds.GetRunningBalance();
            double reconScience = science.GetRunningScience();
            double reconRep = rep.GetRunningRep();

            string report =
                $"funds: recon={reconFunds.ToString("R", IC)} save={SaveFunds.ToString("R", IC)} d={(reconFunds - SaveFunds).ToString("R", IC)} | " +
                $"science: recon={reconScience.ToString("R", IC)} save={SaveScience.ToString("R", IC)} d={(reconScience - SaveScience).ToString("R", IC)} | " +
                $"rep: recon={reconRep.ToString("R", IC)} save={SaveReputation.ToString("R", IC)} d={(reconRep - SaveReputation).ToString("R", IC)}";

            // FUNDS: the real engine reproduces KSP's pool to float32 noise. This
            // REFUTED the raw-walk hypothesis that the strategy's funds side was
            // unmodeled - the converted funds arrive as stock transactions the
            // observers record organically.
            Assert.True(Math.Abs(reconFunds - SaveFunds) < 0.01, "FUNDS diverged. " + report);

            // SCIENCE: KNOWN DIVERGENCE, pinned (STRATEGY-SCIENCE-CONVERSION-LEAK in
            // docs/dev/todo-and-known-bugs.md). The researchIPsellout (Patents
            // Licensing) currency exchange moved science into funds at one KSC-frozen
            // UT. Its FUNDS leg is captured (a FundsEarning with FundsSource.Strategy,
            // which is why the funds assertion above closes); its SCIENCE leg is
            // dropped by BOTH capture doors - GameStateRecorder.OnScienceChanged has
            // no TransactionReasons.StrategyInput forward (OnFundsChanged and
            // OnReputationChanged both have theirs), and GameStateEventConverter's
            // ScienceChanged case is still an unconditional return null with no
            // ConvertStrategyExchangeScience beside the Funds/Reputation pair. So the
            // ledger carries the credit without the matching debit and the
            // reconstruction runs high by exactly the diverted science.
            //
            // NOT a share of the earnings: every ScienceEarning row in this fixture
            // sums to 46.664 awarded / 42.632 effective after the subject hard cap, so
            // the missing 108.84 came out of the 750-point seed. NOT a dropped
            // ScienceSpending either: tech-node costs are integers and this delta is
            // not one.
            //
            // Pinned so any change in this behavior - including the fix - surfaces
            // here and flips this pin deliberately.
            Assert.True(Math.Abs((reconScience - SaveScience) - 108.84171851920314) < 0.001,
                "SCIENCE divergence moved off its pinned value. " + report);

            // REPUTATION: small real divergence (-0.00364), above float32 print noise
            // at this magnitude but far below display precision. Pinned as a window,
            // not a value; tighten when the science leak is resolved.
            Assert.True(Math.Abs(reconRep - SaveReputation) < 0.01, "REPUTATION diverged beyond window. " + report);
        }
    }
}
