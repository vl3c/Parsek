using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A.1 (career-ledger lane): synthetic CHARACTERIZATION cells for the science
    /// earning-vs-spending ordering contract. These assert what the code does today,
    /// not what it ought to do.
    ///
    /// Two seams under test:
    ///   - <see cref="ScienceModule.ProcessSpending"/> deducts the cost in full. When
    ///     the reconstructed balance does not cover it, the running value goes below 0
    ///     (a science debt, design 3.15 step 6) that later earnings repay, and the tech
    ///     stays researched. Until step 6 the walk refused such a spend and kept the
    ///     balance, so the reconstruction ran HIGH by exactly the cost.
    ///   - <see cref="RecalculationEngine.SortActions"/> puts earnings before
    ///     spendings at an EQUAL UT (its secondary key), which is the only thing
    ///     that keeps a same-UT earn-then-spend pair solvent.
    ///
    /// Why synthetic. The c1-era finding (one 90-cost heavyRocketry spend judged
    /// unaffordable at a reconstructed balance of 85.30028700828552, closed by step 6) sits on
    /// a career that predates many fixes, so it cannot be adjudicated as a value
    /// oracle. The MECHANISM does not need that career: ProcessSpending is pure and
    /// the shape reproduces in three actions. That is what these cells pin.
    ///
    /// Distinct from STRATEGY-SCIENCE-CONVERSION-LEAK (docs/dev/todo-and-known-bugs.md),
    /// which is a CAPTURE-side gap measured on the C2Career fixture. This file is
    /// about ordering inside the walk, and asserts no defect.
    /// </summary>
    [Collection("Sequential")]
    public class ScienceSpendingOrderingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ScienceSpendingOrderingTests()
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

        private static ScienceModule RegisterScienceModule()
        {
            var science = new ScienceModule();
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            return science;
        }

        private static GameAction Seed(double ut, float amount)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceInitial,
                InitialScience = amount
            };
        }

        private static GameAction Earning(double ut, string subjectId, float awarded)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                SubjectId = subjectId,
                ScienceAwarded = awarded,
                // Cap set to the award so the subject hard cap never trims these cells.
                SubjectMaxValue = awarded
            };
        }

        private static GameAction Spending(double ut, string nodeId, float cost)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceSpending,
                NodeId = nodeId,
                Cost = cost
            };
        }

        // ================================================================
        // Cell 1 - control: a spend the balance covers deducts normally
        // ================================================================

        [Fact]
        public void Spending_AfterEarning_WithSufficientBalance_Deducts()
        {
            var science = RegisterScienceModule();

            var actions = new List<GameAction>
            {
                Seed(0.0, 100f),
                Earning(100.0, "temperatureScan@KerbinFlyingLow", 50f),
                Spending(200.0, "basicRocketry", 90f)
            };

            RecalculationEngine.Recalculate(actions);

            // 100 seed + 50 earned - 90 spent.
            Assert.Equal(60.0, science.GetRunningScience(), 3);
            Assert.True(actions[2].Affordable, "the spend was covered and must be marked affordable");

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]")
                && l.Contains("Spending: nodeId=basicRocketry")
                && l.Contains("affordable=true"));
            Assert.DoesNotContain(logLines, l => l.Contains("Spending NOT affordable"));
        }

        // ================================================================
        // Cell 2 - the c1 shape: the balance at the spend UT is below the cost
        // ================================================================

        [Fact]
        public void Spending_BeforeTheEarningThatFundsIt_IsChargedIntoDebt_AndLaterEarningRepaysIt()
        {
            var science = RegisterScienceModule();

            // The c1 shape reduced to three actions: the walk reaches a 90-cost node at
            // a reconstructed balance of 85.3, and the earning that would have paid for
            // it carries a LATER UT.
            var actions = new List<GameAction>
            {
                Seed(0.0, 85.3f),
                Spending(10.0, "heavyRocketry", 90f),
                Earning(20.0, "mysteryGoo@KerbinSrfLandedShores", 50f)
            };

            RecalculationEngine.Recalculate(actions);

            // Step 6: charged in full into a science debt, NOT refused, NOT clamped to
            // zero; the tech stays researched.
            Assert.True(actions[1].Affordable, "a charged tech stays researched, debt or not");

            // The balance is the seed minus the cost plus the later earning: the debt was
            // repaid first. Before step 6 the 90 never left and the reconstruction ended one
            // full cost HIGH, the magnitude the c1 finding measured (+90.0 there).
            double repaidBalance = (double)85.3f - 90.0 + 50.0;
            Assert.Equal(repaidBalance, science.GetRunningScience(), 3);

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]")
                && l.Contains("Spending charged into science debt")
                && l.Contains("nodeId=heavyRocketry")
                && l.Contains("cost=90"));
            Assert.DoesNotContain(logLines, l => l.Contains("Spending NOT affordable"));
        }

        // ================================================================
        // Cell 3 - the equal-UT tiebreak is what keeps a same-instant pair solvent
        // ================================================================

        [Fact]
        public void Spending_AtSameUTAsTheEarning_IsAffordable_ViaTheTiebreak()
        {
            var science = RegisterScienceModule();

            // 50 seed + 50 earned = 100 covers the 90 cost, but ONLY because the earning
            // is walked first. The spending is listed first to prove that the input order
            // is not what saves it.
            var actions = new List<GameAction>
            {
                Seed(0.0, 50f),
                Spending(100.0, "heavyRocketry", 90f),
                Earning(100.0, "mysteryGoo@KerbinSrfLandedShores", 50f)
            };

            RecalculationEngine.Recalculate(actions);

            Assert.True(actions[1].Affordable,
                "the equal-UT earning sorts first, so the spend must be affordable");
            Assert.Equal(10.0, science.GetRunningScience(), 3);

            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]")
                && l.Contains("Spending: nodeId=heavyRocketry")
                && l.Contains("affordable=true"));
            Assert.DoesNotContain(logLines, l => l.Contains("Spending charged into science debt"));
        }

        [Fact]
        public void Spending_OneTickBeforeTheEarning_GoesIntoDebt_TheTiebreakIsUTBound()
        {
            var science = RegisterScienceModule();

            // Negative control for the cell above: move the spend one tick EARLIER and
            // the tiebreak no longer applies, because it is secondary to UT.
            var actions = new List<GameAction>
            {
                Seed(0.0, 50f),
                Spending(99.0, "heavyRocketry", 90f),
                Earning(100.0, "mysteryGoo@KerbinSrfLandedShores", 50f)
            };

            RecalculationEngine.Recalculate(actions);

            // Not covered by the equal-UT tiebreak: the spend goes into a 40 debt that the
            // earning repays. The end balance matches the same-UT cell; the difference is
            // the debt line, which only the uncovered spend writes.
            Assert.True(actions[1].Affordable, "a charged tech stays researched, debt or not");
            Assert.Equal(10.0, science.GetRunningScience(), 3);
            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]")
                && l.Contains("Spending charged into science debt")
                && l.Contains("nodeId=heavyRocketry")
                && l.Contains("debt=40"));
        }
    }
}
