using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the rewind read-back divergence guard (audit rec #1,
    /// docs/dev/ledger-state-reconstruction-audit.md §8): the pure two-witness floor
    /// predicate (<see cref="KspStatePatcher.ResolveRewindDivergence"/>), the
    /// per-resource runner (<see cref="KspStatePatcher.RunRewindReadbackGuard"/>) and its
    /// logging, and the warn-only-vs-abort PatchAll integration.
    ///
    /// The guard exists to turn a silent career-corruption clobber (a recalc that writes
    /// the economy BELOW the real career economy) into a caught, logged event. Each test
    /// names the regression it guards. Default behavior is warn-only and must never alter
    /// a successful rewind; the abort path is opt-in (forced here via
    /// <see cref="RewindReadbackGuard.ForceAbortForTesting"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RewindReadbackGuardTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindReadbackGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            KspStatePatcher.SuppressUnityCallsForTesting = true;
            RewindReadbackGuard.ResetForTesting();

            // The science realized-running value folds in two LedgerOrchestrator pending
            // adjusters that read GameStateStore / Ledger / "now UT". Keep them at a
            // deterministic empty state with a fixed clock so the science floor math is
            // not perturbed by a Unity Planetarium read.
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.NowUtProviderForTesting = () => 1000.0;
        }

        public void Dispose()
        {
            LedgerOrchestrator.NowUtProviderForTesting = null;
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = false;
            RewindReadbackGuard.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateRecorder.IsReplayingActions = false;
        }

        // ================================================================
        // Pure predicate — ResolveRewindDivergence
        // ================================================================

        [Fact]
        public void Resolve_TargetEqualsFloor_WithinExpectedRange()
        {
            // Regression: an exact-match target must NOT flag. floor = min(100k,120k) = 100k,
            // target = 100k -> within range.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", 100000.0, 120000.0, 100000.0, KspStatePatcher.RewindReadbackFundsTolerance,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);
            Assert.Equal(100000.0, floor);
            Assert.Equal(0.0, delta);
        }

        [Fact]
        public void Resolve_WithinTolerance_WithinExpectedRange()
        {
            // Regression: float-rounding noise just under the floor must not flag. floor=100k,
            // target=100k-0.5, tolerance=1.0 -> within range.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", 100000.0, 100000.0, 99999.5, 1.0,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);
            Assert.Equal(100000.0, floor);
        }

        [Fact]
        public void Resolve_TargetBelowFloorMinusTolerance_FlaggedDivergence()
        {
            // Regression: the BUG-A clobber. Both witnesses high (100k, 120k -> floor 100k),
            // recalc target collapsed to 60k -> downward divergence flagged.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", 100000.0, 120000.0, 60000.0, 1.0,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.FlaggedDivergence, v);
            Assert.Equal(100000.0, floor);
            Assert.Equal(-40000.0, delta);
        }

        [Fact]
        public void Resolve_TargetFarAboveFloor_WithinExpectedRange()
        {
            // Regression: the false-positive case. A normal career-restore raises the target
            // well ABOVE the floor (career sticks above the old quicksave); must NOT flag.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", 50000.0, 50000.0, 250000.0, 1.0,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);
            Assert.Equal(50000.0, floor);
            Assert.Equal(200000.0, delta);
        }

        [Fact]
        public void Resolve_OnlyOneWitnessFinite_FloorUsesIt()
        {
            // Regression: a null witness must not break the floor. Only eRp finite -> floor=eRp.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "science", null, 500.0, 499.95, 0.1,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);
            Assert.Equal(500.0, floor);

            // The eBefore-only branch (eRp null) is symmetric.
            var v2 = KspStatePatcher.ResolveRewindDivergence(
                "science", 800.0, null, 200.0, 0.1,
                out double floor2, out double delta2);
            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.FlaggedDivergence, v2);
            Assert.Equal(800.0, floor2);
        }

        [Fact]
        public void Resolve_NoFiniteWitness_NotEvaluated()
        {
            // Regression: zero witnesses must not crash or wrongly flag — there is no floor
            // to judge against.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "reputation", null, null, 123.0, 0.1,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.NotEvaluated, v);
            Assert.True(double.IsNaN(floor));
            Assert.True(double.IsNaN(delta));
        }

        [Fact]
        public void Resolve_NonFiniteWitness_IsTreatedAsMissing()
        {
            // A NaN/Inf witness is not a usable floor; here eBefore is NaN so the floor falls
            // back to the finite eRp.
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", double.NaN, 100000.0, 99999.9, 1.0,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);
            Assert.Equal(100000.0, floor);
        }

        [Fact]
        public void Resolve_NaNTarget_FlaggedDivergence()
        {
            // Regression: a NaN recalc target is corruption by definition and must flag even
            // though it is not numerically "below" anything.
            var vNaN = KspStatePatcher.ResolveRewindDivergence(
                "funds", 100000.0, 100000.0, double.NaN, 1.0,
                out double floorN, out double deltaN);
            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.FlaggedDivergence, vNaN);
            Assert.True(double.IsNaN(floorN));
            Assert.True(double.IsNaN(deltaN));

            // Infinity target is equally corrupt.
            var vInf = KspStatePatcher.ResolveRewindDivergence(
                "funds", 100000.0, 100000.0, double.PositiveInfinity, 1.0,
                out double floorI, out double deltaI);
            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.FlaggedDivergence, vInf);
        }

        [Fact]
        public void Resolve_PostRpSpendingSim_LegitDrawdownNotFlagged()
        {
            // Regression: a quicksave-only compare would false-flag legit post-RP spending.
            // After the RP the player SPENT, so eBefore (pre-rewind, lower) < eRp (older
            // quicksave, higher). The recalc target lands at eBefore (the spent-down career).
            // floor = min(eBefore, eRp) = eBefore, so target == floor -> within range. A naive
            // "target vs quicksave (eRp)" check would flag this healthy rewind.
            double eBefore = 33000.0;   // pre-rewind career after spending
            double eRp = 48000.0;       // older RP quicksave, before that spend
            double target = eBefore;    // recalc reproduces the spent-down career
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", eBefore, eRp, target, 1.0,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);
            Assert.Equal(eBefore, floor);
            Assert.Equal(0.0, delta);
        }

        [Fact]
        public void Resolve_ExactlyAtFloorMinusTolerance_WithinExpectedRange()
        {
            // Boundary: target == floor - tolerance is the inclusive lower edge of "within"
            // (the predicate uses >= floor - tolerance).
            double floorVal = 100000.0;
            double tol = 1.0;
            var v = KspStatePatcher.ResolveRewindDivergence(
                "funds", floorVal, floorVal, floorVal - tol, tol,
                out double floor, out double delta);

            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.WithinExpectedRange, v);

            // One ulp below the edge flags.
            var v2 = KspStatePatcher.ResolveRewindDivergence(
                "funds", floorVal, floorVal, floorVal - tol - 0.001, tol,
                out double floor2, out double delta2);
            Assert.Equal(KspStatePatcher.RewindReadbackVerdict.FlaggedDivergence, v2);
        }

        // ================================================================
        // Runner — RunRewindReadbackGuard logging
        // ================================================================

        [Fact]
        public void Runner_FlaggedDivergence_EmitsWarnWithAllFields()
        {
            // Regression: the loud WARN must name resource + both witnesses + floor + target
            // + delta so the corruption is diagnosable from the log alone.
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f); // running 33000
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 100000.0 },
                new EconomySnapshot { Funds = 120000.0 });

            KspStatePatcher.RunRewindReadbackGuard(null, funds, null, authoritativeReduction: false);

            Assert.Contains(logLines, l =>
                l.Contains("[RewindReadback]")
                && l.Contains("FLAGGED DIVERGENCE")
                && l.Contains("resource=funds")
                && l.Contains("eBefore=100000")
                && l.Contains("eRp=120000")
                && l.Contains("floor=100000")
                && l.Contains("target=33000")
                && l.Contains("delta=-67000"));
        }

        [Fact]
        public void Runner_WithinRange_EmitsVerboseNoWarn()
        {
            // Regression: a clean rewind must not log a false alarm. running 33000 with a
            // floor of 30000 -> within range, Verbose only.
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f); // running 33000
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 30000.0 },
                new EconomySnapshot { Funds = 30000.0 });

            KspStatePatcher.RunRewindReadbackGuard(null, funds, null, authoritativeReduction: false);

            Assert.Contains(logLines, l =>
                l.Contains("[RewindReadback]")
                && l.Contains("within-expected-range")
                && l.Contains("resource=funds"));
            Assert.DoesNotContain(logLines, l => l.Contains("FLAGGED DIVERGENCE"));
        }

        [Fact]
        public void Runner_NoFiniteWitness_EmitsVerboseSkipNoWarn()
        {
            // Regression: a resource with no witness must log a skip, not flag.
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f); // running 33000
            RewindReadbackGuard.Arm(new EconomySnapshot(), new EconomySnapshot()); // no witnesses

            KspStatePatcher.RunRewindReadbackGuard(null, funds, null, authoritativeReduction: false);

            Assert.Contains(logLines, l =>
                l.Contains("[RewindReadback]")
                && l.Contains("skipped resource=funds")
                && l.Contains("no finite witness"));
            Assert.DoesNotContain(logLines, l => l.Contains("FLAGGED DIVERGENCE"));
        }

        [Fact]
        public void Runner_NotArmed_ReturnsFalseAndEmitsNothing()
        {
            // Regression: the guard must be inert on ordinary (non-rewind) recalc patches —
            // it must NOT read modules or log when not armed.
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f);
            Assert.False(RewindReadbackGuard.Armed);

            bool abort = KspStatePatcher.RunRewindReadbackGuard(
                null, funds, null, authoritativeReduction: false);

            Assert.False(abort);
            Assert.DoesNotContain(logLines, l => l.Contains("[RewindReadback]"));
        }

        [Fact]
        public void Runner_FlaggedButAbortOff_ReturnsFalse()
        {
            // Regression core guarantee: warn-only never aborts. Flagged divergence but abort
            // opt-in OFF -> runner returns false (PatchAll proceeds normally).
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f); // running 33000
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 100000.0 },
                new EconomySnapshot { Funds = 120000.0 });

            bool abort = KspStatePatcher.RunRewindReadbackGuard(
                null, funds, null, authoritativeReduction: false);

            Assert.False(abort);
            Assert.Contains(logLines, l => l.Contains("FLAGGED DIVERGENCE"));
        }

        [Fact]
        public void Runner_FlaggedAndForceAbort_ReturnsTrue()
        {
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f);
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 100000.0 },
                new EconomySnapshot { Funds = 120000.0 });
            RewindReadbackGuard.ForceAbortForTesting = true;

            bool abort = KspStatePatcher.RunRewindReadbackGuard(
                null, funds, null, authoritativeReduction: false);

            Assert.True(abort);
        }

        // ================================================================
        // PatchAll integration — abort vs warn-only early-return proof
        // ================================================================

        [Fact]
        public void PatchAll_ForceAbortAndArmedDivergence_AbortsBeforeDispatch()
        {
            // Regression: an aborting PatchAll must early-return BEFORE the Patch* dispatch.
            // Proof: the abort WARN is present AND the trailing "patch-all-complete" line is
            // ABSENT (it only logs at the end of a non-aborted PatchAll).
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f); // running 33000 < floor
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 100000.0 },
                new EconomySnapshot { Funds = 120000.0 });
            RewindReadbackGuard.ForceAbortForTesting = true;

            KspStatePatcher.PatchAll(
                science: null, funds: funds, reputation: null,
                milestones: new MilestonesModule(), facilities: new FacilitiesModule());

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("ABORTING rewind ledger patch")
                && l.Contains("loaded quicksave values stand"));
            Assert.DoesNotContain(logLines, l => l.Contains("PatchAll complete"));
        }

        [Fact]
        public void PatchAll_AbortOffAndArmedDivergence_WarnsButCompletes()
        {
            // Regression core guarantee: warn-only never aborts. With abort OFF the divergence
            // WARN is present AND PatchAll runs to its "patch-all-complete" line (no behavior
            // change to a successful rewind).
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f); // running 33000 < floor
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 100000.0 },
                new EconomySnapshot { Funds = 120000.0 });
            // ForceAbortForTesting left false; AbortRewindPatchOnDivergence left false.

            KspStatePatcher.PatchAll(
                science: null, funds: funds, reputation: null,
                milestones: new MilestonesModule(), facilities: new FacilitiesModule());

            Assert.Contains(logLines, l => l.Contains("FLAGGED DIVERGENCE") && l.Contains("resource=funds"));
            Assert.DoesNotContain(logLines, l => l.Contains("ABORTING rewind ledger patch"));
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void PatchAll_NotArmed_NoGuardLinesNoAbort()
        {
            // The guard is inert on a normal recalc patch (not a rewind): no RewindReadback
            // lines, PatchAll completes.
            var funds = BuildFundsModule(seed: 25000f, earn: 8000f);

            KspStatePatcher.PatchAll(
                science: null, funds: funds, reputation: null,
                milestones: new MilestonesModule(), facilities: new FacilitiesModule());

            Assert.DoesNotContain(logLines, l => l.Contains("[RewindReadback]"));
            Assert.DoesNotContain(logLines, l => l.Contains("ABORTING rewind ledger patch"));
            Assert.Contains(logLines, l => l.Contains("PatchAll complete"));
        }

        // ================================================================
        // Arm / Clear lifecycle
        // ================================================================

        [Fact]
        public void Arm_LogsAllWitnessValues_ThenClearDisarms()
        {
            RewindReadbackGuard.Arm(
                new EconomySnapshot { Funds = 100000.0, Science = 500.0, Reputation = 42f },
                new EconomySnapshot { Funds = 120000.0, Science = 600.0, Reputation = 50f });

            Assert.True(RewindReadbackGuard.Armed);
            Assert.Contains(logLines, l =>
                l.Contains("[RewindReadback]")
                && l.Contains("armed rewind read-back guard")
                && l.Contains("eBeforeFunds=100000")
                && l.Contains("eRpFunds=120000")
                && l.Contains("eBeforeScience=500")
                && l.Contains("eBeforeRep=42"));

            RewindReadbackGuard.Clear();
            Assert.False(RewindReadbackGuard.Armed);
        }

        // ================================================================
        // Helpers
        // ================================================================

        // Builds a FundsModule walked to a deterministic running balance (seed + earn) via
        // the same ComputeTotalSpendings/ProcessAction pattern DrawdownGuardTests uses.
        private static FundsModule BuildFundsModule(float seed, float earn)
        {
            var module = new FundsModule();
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = seed },
                new GameAction { UT = 10, Type = GameActionType.FundsEarning, FundsAwarded = earn, FundsSource = FundsEarningSource.Milestone },
            };
            module.ComputeTotalSpendings(actions);
            module.ProcessAction(actions[0]);
            module.ProcessAction(actions[1]);
            return module;
        }
    }
}
