using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase D (career-ledger lane, tasks D.1 + D.3): the c1 career committed as a
    /// headless replay REGRESSION FLOOR.
    ///
    /// ==================================================================
    /// READ THIS BEFORE READING A RESULT OFF THIS FILE
    /// ==================================================================
    ///
    /// c1 IS A FLOOR, NOT AN ORACLE. It is the richest ledger on the machine - 323
    /// GAME_ACTION rows across a full career span, an order of magnitude past every
    /// other committed fixture - but it was recorded on 2026-08-09, BEFORE the
    /// 2026-08 fix wave (PR #1498's capture fixes, PR #1483's strategy two-door, the
    /// 2026-08-20 milestone-rep residual-step fix, and others). A green or a red
    /// against c1 is therefore NOT a statement about current CAPTURE behavior.
    ///
    /// What committing it buys is exactly two things:
    ///
    ///   1. A REGRESSION FLOOR. The recalculation engine's walk over a large real
    ///      ledger is pinned end to end, so a future ENGINE change that moves any of
    ///      the three pools reds here instead of going unnoticed. Nothing else in the
    ///      suite exercises the walk at this scale - the next largest committed
    ///      ledger is C2Career's 68 rows, and most career cells build 1-4 synthetic
    ///      actions from a clean slate.
    ///
    ///   2. CLOSURE OF THE PHASE D CLASSIFICATION (D.1). The historical +90.0 science
    ///      divergence is CHARACTERIZED here - value pinned, mechanism named, era
    ///      bounded - not ADJUDICATED. This file asserts what the walk DOES. It never
    ///      asserts what the walk OUGHT to do.
    ///
    /// DO NOT FILE A c1-DERIVED DIVERGENCE AS A PRODUCT FINDING. Every divergence
    /// below is a joint fact about (era-bound ledger rows) x (current engine), and
    /// the first factor cannot be re-measured without re-playing the career. If one
    /// of these numbers looks like a defect, the way to find out is to reproduce it
    /// on a POST-fix career (`C2CareerPostFix`, or a fresh harvest), never to open a
    /// finding off this fixture.
    ///
    /// ------------------------------------------------------------------
    /// THE MEASUREMENT (2026-08-20, current engine), against pools KSP itself wrote:
    ///
    ///   pool         reconstructed          save                 delta
    ///   FUNDS        400199.27429199219     400199.27429199219   0 (exact)
    ///   SCIENCE      119.30025362968445     29.3002529           +90.000000729684444
    ///   REPUTATION   45.590347290039063     45.5432281           +0.047119190039062175
    ///
    /// vs the 2026-08-09 measurement recorded in
    /// `docs/dev/plans/career-ledger-coverage.md` Phase D:
    ///
    ///   FUNDS        unchanged, still bit-exact.
    ///   SCIENCE      unchanged (+90.0-shaped, same proximate cause - see below).
    ///   REPUTATION   MOVED. It was 45.54322814941406 (delta ~4.9e-08, "reproduces
    ///                to float32"). See the reputation note on the replay cell.
    ///
    /// ------------------------------------------------------------------
    /// SEEDS. c1's ledger baseline was seeded MID-career: FundsInitial=72840,
    /// ScienceInitial=123.763962, ReputationInitial=0 - which do NOT match a career
    /// start (25000 / 0 / 0). That is expected and is not a defect: the replay runs
    /// from the ledger's OWN seeds, exactly as the C2 replays do. What it means for
    /// reading this file is that the walk reconstructs a career SUFFIX, so no cell
    /// here may reason from stock career-start constants.
    ///
    /// NO KerbalExperience ROWS. The ledger carries zero `type = 31`
    /// (KerbalExperience) rows - it predates P9a entirely. So this fixture pins the
    /// walk over eleven action types (0/1/2/3/4/5/6/11/20/21/22) and is silent about
    /// the experience module.
    ///
    /// Non-circular by construction, like both C2 replays: KSP wrote the save pools,
    /// Parsek's observers wrote the ledger, and this test diffs the two independent
    /// producers headlessly. The replay runs the PRODUCTION registration
    /// (`LedgerOrchestrator.Initialize()`), so the module graph under replay is the
    /// one production builds - a hand-rolled mirror would be a second copy to keep in
    /// step, and a drifted copy would replay a graph production never uses.
    ///
    /// FIXTURE PROVENANCE. `Source/Parsek.Tests/Fixtures/C1Career/` holds verbatim
    /// copies (sha256-verified at copy time) of two files from the c1 save snapshot
    /// taken 2026-08-09:
    ///
    ///   Parsek/GameState/ledger.pgld   6db8eeef305e43351626b58cb0a93e1ca3d08bfb2e6be007f8397cc91fa267c9
    ///   persistent.sfs                 c86532644ed1e5bc6955b07a55b4d8178b61a218020707f024cca35aa21df9e9
    ///
    /// The layout mirrors the `C2Career` fixture. Only these two files are committed:
    /// the replay needs the ledger and the save pools, and nothing here reads
    /// recordings, milestones, events, or baselines.
    /// </summary>
    [Collection("Sequential")]
    public class C1CareerLedgerReplayTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Pinned from the fixture's persistent.sfs (KSP-authored ground truth).
        private const double SaveFunds = 400199.27429199219;
        private const double SaveScience = 29.3002529;
        private const double SaveReputation = 45.5432281;

        // Pinned from the fixture's ledger seeds (types 20/21/22). MID-career, not a
        // career start - see the class note.
        private const double SeedFunds = 72840.0;
        private const double SeedScience = 123.763962;
        private const double SeedReputation = 0.0;

        // The one row that trips ScienceModule.ProcessSpending's affordability gate.
        private const string RefusedNodeId = "heavyRocketry";
        private const double RefusedNodeUT = 475457.99765590986;
        private const double RefusedNodeCost = 90.0;

        // Measured 2026-08-20 on current code. See the class note for the full table
        // and for what these numbers are and are not evidence of.
        private const double ReconScience = 119.30025362968445;
        private const double ScienceDelta = 90.000000729684444;
        private const double ReconReputation = 45.590347290039063;
        private const double ReputationDelta = 0.047119190039062175;

        public C1CareerLedgerReplayTests()
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
            string dir = Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "C1Career");
            Assert.True(Directory.Exists(dir), $"C1Career fixture dir not found at '{dir}'");
            return dir;
        }

        private static List<GameAction> LoadFixtureLedger()
        {
            string path = Path.Combine(ResolveFixtureDir(), "Parsek", "GameState", "ledger.pgld");
            Assert.True(File.Exists(path), $"fixture ledger not found at '{path}'");
            Assert.True(Ledger.LoadFromFile(path), "Ledger.LoadFromFile failed on the C1Career fixture");
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
        // D.3 - structural floor: the fixture itself
        // ================================================================

        [Fact]
        public void FixtureLedger_LoadsAllThreeHundredTwentyThreeActions()
        {
            var actions = LoadFixtureLedger();
            Assert.Equal(323, actions.Count);
        }

        [Fact]
        public void FixtureLedger_CarriesTheMidCareerSeeds()
        {
            // MID-career seeds, deliberately pinned: they are the walk's starting point
            // and every reconstructed pool below is measured from them, so a codec change
            // that silently dropped or defaulted a seed would otherwise surface only as a
            // pool delta with no cause attached.
            var actions = LoadFixtureLedger();

            var funds = actions.FindAll(a => a.Type == GameActionType.FundsInitial);
            var science = actions.FindAll(a => a.Type == GameActionType.ScienceInitial);
            var rep = actions.FindAll(a => a.Type == GameActionType.ReputationInitial);

            Assert.Single(funds);
            Assert.Single(science);
            Assert.Single(rep);

            Assert.Equal(SeedFunds, funds[0].InitialFunds, 3);
            Assert.Equal(SeedScience, science[0].InitialScience, 4);
            Assert.Equal(SeedReputation, rep[0].InitialReputation, 4);

            // The seeds are NOT a career start (25000 / 0 / 0). Stated as an assertion so
            // a future reader cannot mistake this fixture for a from-scratch career.
            Assert.NotEqual(25000.0, funds[0].InitialFunds, 3);
        }

        [Fact]
        public void FixtureLedger_CarriesNoKerbalExperienceRows()
        {
            // c1 predates P9a, so the experience module is untouched by this floor.
            // Pinned positively so nobody reads a green run here as coverage of it.
            var actions = LoadFixtureLedger();
            Assert.Empty(actions.FindAll(a => a.Type == GameActionType.KerbalExperience));
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
        // D.1 - the science characterization, stated STRUCTURALLY
        // ================================================================

        [Fact]
        public void FixtureLedger_CarriesTheUnaffordableHeavyRocketrySpend()
        {
            // The mechanism marker for the +90 science divergence, asserted on the DATA
            // rather than on the outcome: one ScienceSpending row, cost 90, at the UT the
            // 2026-08-09 analysis named. This is what makes the pinned magnitude on the
            // replay cell legible - if a future codec or sort change moved this row, the
            // divergence's explanation would move with it and this cell would say so
            // before the magnitude cell did.
            var actions = LoadFixtureLedger();

            var row = actions.Find(a =>
                a.Type == GameActionType.ScienceSpending
                && a.NodeId == RefusedNodeId
                && Math.Abs(a.UT - RefusedNodeUT) < 0.001);

            Assert.NotNull(row);
            Assert.Equal(RefusedNodeCost, row.Cost, 3);
        }

        [Fact]
        public void RealEngine_RefusesTheHeavyRocketrySpend_AndSaysSoInTheLog()
        {
            // The mechanism, observed at the seam. ScienceModule.ProcessSpending deducts
            // only when the reconstructed balance already covers the cost, and otherwise
            // WARNs and leaves the balance untouched (ScienceSpendingOrderingTests pins
            // that behavior synthetically and fixture-independently). Here it is caught
            // firing on the REAL c1 walk, on the one row that trips it.
            //
            // CHARACTERIZATION, NOT ADJUDICATION. Whether the pre-fix-era ledger SHOULD
            // have carried enough science at this UT to cover the node is a question about
            // 2026-08-09 capture behavior that this fixture cannot answer.
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            var actions = LoadFixtureLedger();
            RecalculationEngine.Recalculate(actions);

            var refusals = logLines.FindAll(l =>
                l.Contains("[ScienceModule]") && l.Contains("Spending NOT affordable"));

            // Exactly one refusal in the whole 323-row walk, and it is heavyRocketry.
            Assert.Single(refusals);
            Assert.Contains("nodeId=" + RefusedNodeId, refusals[0]);
            Assert.Contains("cost=90", refusals[0]);
        }

        // ================================================================
        // D.3 - the regression floor: real engine vs KSP's own save
        // ================================================================

        [Fact]
        public void RealEngine_ReplaysC1Ledger_PoolsVsSave()
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

            // ------------------------------------------------------------------
            // FUNDS: BIT-EXACT, and unmoved since 2026-08-09.
            //
            // This is the strongest single statement the fixture makes. 323 rows - 125
            // FundsSpending, 14 FundsEarning, 40 milestones, 11 ContractComplete rows over
            // only 4 distinct contracts (the first-tier ContractsModule is what collapses
            // those to one effective completion each) - and the walk lands on KSP's own
            // pool with a delta of ZERO, not of float noise. Pinned as equality rather
            // than a window, because a window here would hide a real regression under a
            // tolerance the measurement does not need.
            Assert.Equal(SaveFunds, reconFunds, 6);

            // ------------------------------------------------------------------
            // SCIENCE: the CHARACTERIZED +90 divergence (Phase D task D.1).
            //
            // The walk reconstructs 119.30025362968445 against a save pool of 29.3002529
            // - high by 90.000000729684444, i.e. by exactly one refused tech-node cost
            // plus float32 representation noise. The proximate cause is the refusal cell
            // above: ScienceModule.ProcessSpending judges the 90-cost `heavyRocketry` node
            // unaffordable against the reconstructed balance at ut=475457.99765590986, so
            // the 90 is never deducted and every later balance runs one full cost high.
            //
            // THIS IS A CHARACTERIZATION OF A PRE-FIX-ERA LEDGER, NOT A DEFECT CLAIM.
            // Two independent things could produce it and this fixture cannot separate
            // them: (a) the 2026-08-09 capture layer under-recorded the science available
            // at that UT, so the walk is faithfully replaying a short ledger; (b) the walk
            // orders or values something differently from KSP. Settling that needs a
            // POST-fix career that reaches an unaffordable spend - and per the Phase C
            // ceiling a KscAction forge cannot manufacture one, because the verb
            // pre-refuses an unaffordable research at the door.
            //
            // What IS settled, and settled without this fixture, is the MECHANISM:
            // ScienceSpendingOrderingTests reproduces the whole shape in three synthetic
            // actions (refusal, no clamp, and the equal-UT earning-before-spending
            // tiebreak that keeps a same-instant pair solvent), so nothing about the
            // ordering contract depends on c1 being trustworthy.
            //
            // Pinned so any RECALC-side change in this behavior surfaces here. A move
            // means the engine changed: the fixture is frozen and a capture-side fix
            // cannot retro-fill a committed ledger.pgld.
            Assert.True(Math.Abs(reconScience - ReconScience) < 1e-6,
                "SCIENCE reconstruction moved off its pinned value. " + report);
            Assert.True(Math.Abs((reconScience - SaveScience) - ScienceDelta) < 1e-6,
                "SCIENCE divergence moved off its pinned value. " + report);

            // ------------------------------------------------------------------
            // REPUTATION: MOVED SINCE 2026-08-09, BY A KNOWN RECALC-SIDE CHANGE.
            //
            // On 2026-08-09 this fixture's reputation reconstructed to 45.54322814941406
            // against a save pool of 45.5432281 - a delta of ~4.9e-08, i.e. float32
            // noise. Today it reconstructs 45.590347290039063, high by
            // 0.047119190039062175 (about 0.1% of the pool).
            //
            // The move is attributable to commit 817773dcb (2026-08-20), "Size the
            // reputation residual step from the accumulated actual": KSP's
            // `Reputation.addReputation_granular` tops an award back up with
            // `ModifyReputationDelta(value - num2)` where `num2` is the accumulated
            // POST-CURVE total, and ReputationModule.ApplyReputationCurve had computed
            // that residual as `nominal - (delta * num)` - identically zero for an integer
            // nominal, so the top-up never fired and every integer award landed short by
            // the curve loss. That fix CLOSED the reputation divergence on both C2
            // fixtures (C2CareerPostFix -0.001482 -> +2.4e-07, C2Career -0.00364 ->
            // -1.7e-09) without either being re-harvested, because it is RECALC-side.
            //
            // The same recalc-side property is why it legitimately moves THIS frozen
            // fixture too - and here it moved AWAY from the save pool rather than toward
            // it. That is worth recording plainly, and it is NOT adjudicated here:
            //
            //   - c1 is not an oracle. Its rep-bearing rows (40 MilestoneAchievement, 11
            //     ContractComplete) were captured on 2026-08-09, before the 2026-08 fix
            //     wave, so a rep-row-capture defect of that era is fully consistent with
            //     the numbers above and is indistinguishable from a curve defect FROM
            //     THIS FIXTURE ALONE.
            //   - c1 is nonetheless the only committed career that exercises the curve at
            //     a large |rep| (45.5, against roughly 2 and 5 on the two C2 fixtures),
            //     and the fix's own commit message notes the pre-fix error grew with
            //     |rep|. So this is the right place to NOTICE the question, and the wrong
            //     place to answer it.
            //
            // ANSWERED 2026-08-20, and not by another career - by KSP itself.
            // ReputationCurve_HighRep_AgreesWithStocksGranularPool (StrategyLifecycle,
            // SPACECENTER) walks ten INTEGER awards through stock's own
            // Reputation.AddReputation from rep 60, reads KSP's pool after each, and runs
            // BOTH candidate residual formulas over the same nominals from the same start.
            // Integer nominals are the discriminator: for an integer award the PRE-fix
            // residual (nominal - delta * num) is identically zero so the top-up never
            // fires, while the shipping residual (nominal - accumulated) tops it back up.
            // Run 2026-08-20_2052_L3-strategy-currency-conversion, PASS attempt 1:
            //
            //   RepCurveHighRep VERDICT: agreesWith=current startRep=60 awards=10
            //     kspFinal=99.9952545 currentModelFinal=99.9952545
            //     preFixModelFinal=99.30036 errCurrent=0
            //     errPreFix=0.69489288330078125
            //
            // errCurrent was EXACTLY 0 at eight of the ten steps and one float32 ulp
            // (7.62939453E-06) at the other two, across a -5 step as well as the +5s. So
            // the curve is right at |rep| 60-100 and this fixture's +0.047 is a
            // CAPTURE-ERA artifact of its 2026-08 rows, not a curve defect. The two live
            // pool readings are additionally pinned headlessly by
            // ReputationModuleTests.ApplyReputationCurve_HighRep_ReproducesStocksMeasuredPool.
            //
            // The pin below therefore stays exactly what it always was - a REGRESSION
            // FLOOR for the recalc arithmetic - and it is now backed rather than merely
            // hedged. A future move in these numbers is still a recalc-side change to
            // read, not a licence to re-pin.
            Assert.True(Math.Abs(reconRep - ReconReputation) < 1e-6,
                "REPUTATION reconstruction moved off its pinned value. " + report);
            Assert.True(Math.Abs((reconRep - SaveReputation) - ReputationDelta) < 1e-6,
                "REPUTATION divergence moved off its pinned value. " + report);
        }
    }
}
