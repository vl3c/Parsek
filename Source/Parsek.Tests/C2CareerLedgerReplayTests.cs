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
    /// The replay runs the PRODUCTION registration: LedgerOrchestrator.Initialize()
    /// registers all nine modules in its own tier order and the test reads the pools
    /// back through its module accessors. A hand-rolled mirror of that registration
    /// would be a second copy to keep in step, and a drifted copy would replay a
    /// module graph production never uses.
    ///
    /// TWO-FIXTURE DESIGN (2026-08-19). This file is the PRE-FIX half. Its sibling
    /// <see cref="C2CareerPostFixReplayTests"/> replays `Fixtures/C2CareerPostFix`, a
    /// career EARNED by a driven harness flight on post-fix code. The two are additive,
    /// not successive: this fixture is a HAND-PLAYED career carrying a strategy currency
    /// exchange, the sibling is a DRIVEN flight-earned career carrying a vessel recovery
    /// and no strategy at all. Nothing here is retired or re-measured by it - the
    /// condition for retiring the science pin below is a re-harvest of c2 ITSELF on
    /// post-fix code, which has not happened. What the sibling contributes is
    /// separation: it exercises the earning, seed, recovery and milestone paths in
    /// isolation from any strategy, which is what lets the reputation note below name a
    /// suspect at last.
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

        public C2CareerLedgerReplayTests()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            LedgerOrchestrator.Initialize();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Fixture resolution
        // ================================================================

        private static string ResolveFixtureDir()
        {
            // Shared root resolver: walks up probing for Source/Parsek.sln rather than
            // hard-coding how deep the xUnit output directory sits.
            string root = SyntheticRecordingTests.ResolveProjectRoot();
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
        // A.0 - the leak stated STRUCTURALLY (fixture-independent)
        // ================================================================

        [Fact]
        public void FixtureLedger_StrategyExchange_HasAFundsCreditAndNoScienceDebit()
        {
            // STRATEGY-SCIENCE-CONVERSION-LEAK stated by SHAPE, not by magnitude. The
            // pinned-delta cell below proves the leak is exactly 108.84 points on THIS
            // fixture; this one proves the STRUCTURE that produced it, so it survives a
            // re-forged fixture: at the strategy's UT the ledger carries the currency
            // exchange's FUNDS credit (FundsEarning, FundsSource.Strategy) and carries
            // NO science row of any kind - neither the matching debit nor anything else.
            // Both capture doors dropped the science leg at the time this ledger was
            // written.
            //
            // FROZEN PRE-FIX DATA. The fix is CAPTURE-side (the StrategyInput forward in
            // GameStateRecorder.OnScienceChanged, ConvertStrategyExchangeScience, and
            // GameActionType.StrategyScienceDebit): it changes what NEW recordings
            // capture and cannot retro-fill this committed ledger.pgld. So this cell
            // keeps asserting the pre-fix shape and keeps PASSING after the fix.
            //
            // The predicate below therefore includes StrategyScienceDebit, the row a
            // post-fix capture WOULD write. That is what makes a post-fix re-harvest of
            // c2 actually FLIP this cell instead of leaving it silently green against a
            // fixture that now carries the debit: a predicate naming only the pre-fix
            // science types could never see the new row.
            var actions = LoadFixtureLedger();

            var strategyFundsRows = actions.FindAll(a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.Strategy);
            Assert.NotEmpty(strategyFundsRows);

            foreach (var credit in strategyFundsRows)
            {
                // No science row of ANY type shares the exchange's UT. Post-fix the debit
                // would be a StrategyScienceDebit; pre-fix a debit could only have been a
                // ScienceSpending, and an offsetting credit a ScienceEarning. None
                // exists - that IS the leak.
                var scienceRowsAtUT = actions.FindAll(a =>
                    Math.Abs(a.UT - credit.UT) < 0.001
                    && (a.Type == GameActionType.ScienceSpending
                        || a.Type == GameActionType.ScienceEarning
                        || a.Type == GameActionType.StrategyScienceDebit));
                Assert.True(scienceRowsAtUT.Count == 0,
                    $"expected NO science row at the strategy exchange UT " +
                    $"{credit.UT.ToString("R", IC)}; found {scienceRowsAtUT.Count.ToString(IC)}. " +
                    "This fixture is frozen pre-fix data; a capture-side fix cannot move " +
                    "it. Movement here means the FIXTURE changed (a re-harvest), not the " +
                    "product - and on a re-harvest the expectation becomes 'a " +
                    "StrategyScienceDebit shares the credit's UT'.");
            }
        }

        // ================================================================
        // A.0 - the adjudication: real engine vs KSP's own save
        // ================================================================

        [Fact]
        public void RealEngine_ReplaysC2Ledger_PoolsVsSave()
        {
            // PRODUCTION registration: the ctor called LedgerOrchestrator.Initialize(),
            // so the module graph under replay is the one production builds.
            var actions = LoadFixtureLedger();

            RecalculationEngine.Recalculate(actions);

            double reconFunds = LedgerOrchestrator.Funds.GetRunningBalance();
            double reconScience = LedgerOrchestrator.Science.GetRunningScience();
            double reconRep = LedgerOrchestrator.Reputation.GetRunningRep();

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
            // UT. Its FUNDS leg was captured (a FundsEarning with FundsSource.Strategy,
            // which is why the funds assertion above closes); its SCIENCE leg was
            // dropped by BOTH capture doors AT THE TIME THIS FIXTURE WAS RECORDED -
            // GameStateRecorder.OnScienceChanged had no TransactionReasons.StrategyInput
            // forward (OnFundsChanged and OnReputationChanged both had theirs), and
            // GameStateEventConverter's ScienceChanged case was an unconditional return
            // null with no ConvertStrategyExchangeScience beside the Funds/Reputation
            // pair. So the ledger carries the credit without the matching debit and the
            // reconstruction runs high by exactly the diverted science.
            //
            // BOTH DOORS ARE NOW CLOSED: OnScienceChanged forwards StrategyInput to the
            // ledger, and ConvertEvent routes ScienceChanged to
            // ConvertStrategyExchangeScience, which emits
            // GameActionType.StrategyScienceDebit. This fixture PREDATES that fix and a
            // capture-side fix cannot retro-fill a committed ledger.pgld, so the
            // 108.84171851920314 delta is a DATA-ERA pin over pre-fix data and stays
            // exactly where it is. If this cell ever moves WITHOUT the fixture being
            // re-harvested, a RECALC-side change has occurred and must be investigated -
            // a capture-side fix cannot move it.
            //
            // NOT a share of the earnings: every ScienceEarning row in this fixture
            // sums to 46.664 awarded / 42.632 effective after the subject hard cap, so
            // the missing 108.84 came out of the 750-point seed. NOT a dropped
            // ScienceSpending either: tech-node costs are integers and this delta is
            // not one.
            //
            // Pinned so any RECALC-side change in this behavior surfaces here.
            //
            // THE POST-FIX SIBLING EXISTS AND DOES NOT RETIRE THIS PIN.
            // `C2CareerPostFixReplayTests` replays a career earned by a driven flight on
            // post-fix code - and it carries NO strategy leg at all, so it cannot
            // exercise the exchange this pin is about. It is a second fixture answering
            // a different question, not a re-harvest of c2. The condition for replacing
            // the magnitude above with a closure assertion is unchanged and still unmet:
            // c2 itself re-harvested on post-fix code.
            //
            // What the sibling DOES establish about science is narrower and useful: the
            // flight-EARNING path reconstructs exactly (three subjects, to 1e-4), so a
            // future c2 re-harvest that still diverges would be diverging on the
            // exchange rather than on earnings generally.
            Assert.True(Math.Abs((reconScience - SaveScience) - 108.84171851920314) < 0.001,
                "SCIENCE divergence moved off its pinned value. " + report);

            // REPUTATION: small real divergence (-0.00364), above float32 print noise
            // at this magnitude but far below display precision. Pinned as a window,
            // not a value.
            //
            // THE POST-FIX FIXTURE IS NOW HARVESTED, and it NARROWED this rather than
            // settling it - so the window stays as it is, deliberately, rather than
            // being tightened onto a number measured on different data.
            // `C2CareerPostFixReplayTests` replays a driven flight-earned career with NO
            // strategy leg, NO contracts, a correct rep seed and exactly two +1 milestone
            // awards, and reconstructs reputation 0.00148 LOW. That rules out the
            // strategy exchange and the seed as the cause here and points at the
            // MILESTONE reputation award path, which this fixture also exercises.
            // Neither magnitude is chased yet; see
            // CAREER-MILESTONE-REP-AWARD-RECONSTRUCTS-LOW.
            Assert.True(Math.Abs(reconRep - SaveReputation) < 0.01, "REPUTATION diverged beyond window. " + report);
        }
    }
}
