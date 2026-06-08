using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers the "Keep what you earned" recalc-patch drawdown guard
    /// (docs/dev/plans/recalc-patch-drawdown-guard.md): the pure decision helpers
    /// (IsGuardableDrawdown, ApplyDrawdownGuard, IsAuthoritativeReduction), the
    /// clamp WARN + session-latched ScreenMessage, and the two blocker regressions
    /// (B1 plain-rewind signal-5; B2 reservation drawdown of the spendable target).
    ///
    /// The live PatchFunds/PatchScience/PatchReputation entry points early-return on
    /// null KSP singletons under xUnit, so the clamp arithmetic is covered through the
    /// pure helpers and the WARN/toast through the EmitDrawdownGuardClamp seam; the live
    /// AddFunds/SetScience/SetReputation path is exercised in
    /// RuntimeTests.DrawdownGuard_ClampsLeakAndAuthorizesSignal.
    /// </summary>
    [Collection("Sequential")]
    public class DrawdownGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<(string text, float duration)> screenMessages =
            new List<(string, float)>();

        public DrawdownGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting =
                (text, duration) => screenMessages.Add((text, duration));
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // IsGuardableDrawdown — keyed on the running balance (Blocker 2)
        // ================================================================

        [Fact]
        public void IsGuardableDrawdown_RunningMoreThanEpsilonBelowLive_IsGuardable()
        {
            // running 60000 vs live 100000, eps 0.01 -> true (the BUG-A leak signature)
            Assert.True(KspStatePatcher.IsGuardableDrawdown(60000.0, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableDrawdown_RunningWithinEpsilonBelowLive_IsNotGuardable()
        {
            // running 0.005 below live, eps 0.01 -> false (rounding noise)
            Assert.False(KspStatePatcher.IsGuardableDrawdown(99999.995, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableDrawdown_RunningExactlyEpsilonBelowLive_IsNotGuardable()
        {
            // strict "< live - epsilon": exactly at the boundary is NOT guardable
            Assert.False(KspStatePatcher.IsGuardableDrawdown(100000.0 - 0.01, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableDrawdown_RunningEqualsLive_IsNotGuardable()
        {
            Assert.False(KspStatePatcher.IsGuardableDrawdown(100000.0, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableDrawdown_RunningAboveLive_IsNotGuardable()
        {
            // an increase never trips the guard
            Assert.False(KspStatePatcher.IsGuardableDrawdown(120000.0, 100000.0, 0.01));
        }

        // ================================================================
        // IsGuardableUplift — symmetric mirror keyed on the running balance (Bug 2)
        // ================================================================

        [Fact]
        public void IsGuardableUplift_RunningMoreThanEpsilonAboveLive_IsGuardable()
        {
            // running 415466 vs live 66386, eps 0.01 -> true (the Bug-2 refund signature)
            Assert.True(KspStatePatcher.IsGuardableUplift(415466.0, 66386.0, 0.01));
        }

        [Fact]
        public void IsGuardableUplift_RunningWithinEpsilonAboveLive_IsNotGuardable()
        {
            // running 0.005 above live, eps 0.01 -> false (rounding noise)
            Assert.False(KspStatePatcher.IsGuardableUplift(100000.005, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableUplift_RunningExactlyEpsilonAboveLive_IsNotGuardable()
        {
            // strict "> live + epsilon": exactly at the boundary is NOT guardable
            Assert.False(KspStatePatcher.IsGuardableUplift(100000.0 + 0.01, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableUplift_RunningEqualsLive_IsNotGuardable()
        {
            Assert.False(KspStatePatcher.IsGuardableUplift(100000.0, 100000.0, 0.01));
        }

        [Fact]
        public void IsGuardableUplift_RunningBelowLive_IsNotGuardable()
        {
            // a decrease is the OTHER guard (IsGuardableDrawdown), never the uplift guard
            Assert.False(KspStatePatcher.IsGuardableUplift(60000.0, 100000.0, 0.01));
        }

        // ================================================================
        // IsAuthoritativeReduction — five inputs (Blocker 1 adds the fifth)
        // ================================================================

        [Fact]
        public void IsAuthoritativeReduction_AllFalse_IsFalse()
        {
            Assert.False(LedgerOrchestrator.IsAuthoritativeReduction(
                false, false, false, false, false));
        }

        [Fact]
        public void IsAuthoritativeReduction_IsRewinding_IsTrue()
        {
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                true, false, false, false, false));
        }

        [Fact]
        public void IsAuthoritativeReduction_ReFlyMarker_IsTrue()
        {
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                false, true, false, false, false));
        }

        [Fact]
        public void IsAuthoritativeReduction_MergeJournal_IsTrue()
        {
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                false, false, true, false, false));
        }

        [Fact]
        public void IsAuthoritativeReduction_TombstonePath_IsTrue()
        {
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                false, false, false, true, false));
        }

        [Fact]
        public void IsAuthoritativeReduction_RewindResourceAdjustment_IsTrue()
        {
            // The NEW fifth signal (Blocker 1)
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                false, false, false, false, true));
        }

        [Fact]
        public void IsAuthoritativeReduction_Combinations_IsTrue()
        {
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                true, true, false, false, true));
        }

        // ================================================================
        // ApplyDrawdownGuard — clamp arithmetic
        // ================================================================

        [Fact]
        public void ApplyDrawdownGuard_RunningAboveLive_ReturnsTargetUnchanged_ReservationCase()
        {
            // Blocker 2 / B2 regression: running >= live (intact) but the
            // reservation-aware target is BELOW live. Must NOT clamp; the reservation
            // target is written so overspend stays prevented.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 70000.0, runningBalance: 100000.0, currentLive: 100000.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped);

            Assert.False(clamped);
            Assert.Equal(70000.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_RunningBelowLive_Authoritative_ReturnsTargetUnchanged()
        {
            // B1 regression: running < live but an authoritative signal is set -> apply
            // the legitimate (time-travel) reduction unchanged.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 60000.0, runningBalance: 60000.0, currentLive: 100000.0,
                epsilon: 0.01, authoritativeReduction: true, resource: "funds",
                out bool clamped);

            Assert.False(clamped);
            Assert.Equal(60000.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_RunningBelowLive_NoSignal_ClampsToLive()
        {
            // The missing-earning leak: running < live, no signal -> clamp to live.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 60000.0, runningBalance: 60000.0, currentLive: 100000.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped);

            Assert.True(clamped);
            Assert.Equal(100000.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_TargetAboveLive_ReturnsTargetWhenClamped()
        {
            // max(target, live): if a clamp fires but target somehow exceeds live, the
            // higher value is kept. (Defensive; on a real leak target <= live.)
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 110000.0, runningBalance: 60000.0, currentLive: 100000.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped);

            Assert.True(clamped);
            Assert.Equal(110000.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_EpsilonNoOp_DoesNotClamp()
        {
            // running within epsilon of live -> not guardable -> no clamp.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 99999.999, runningBalance: 99999.999, currentLive: 100000.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped);

            Assert.False(clamped);
            Assert.Equal(99999.999, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_NegativeRepRange_ClampsCorrectly()
        {
            // Reputation can be negative. running -30 < live -10 with no signal -> clamp
            // to live (-10), preserving the player's (higher) reputation.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: -30.0, runningBalance: -30.0, currentLive: -10.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "reputation",
                out bool clamped);

            Assert.True(clamped);
            Assert.Equal(-10.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_NegativeRange_AuthorizedReductionApplies()
        {
            // A legitimate (signal-set) reputation penalty into the negative range applies.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: -30.0, runningBalance: -30.0, currentLive: -10.0,
                epsilon: 0.01, authoritativeReduction: true, resource: "reputation",
                out bool clamped);

            Assert.False(clamped);
            Assert.Equal(-30.0, result);
        }

        // ================================================================
        // ApplyDrawdownGuard — symmetric DOWN (uplift) clamp + direction (Bug 2)
        // ================================================================

        [Fact]
        public void ApplyDrawdownGuard_RunningAboveLive_NoSignal_ClampsDownToLive()
        {
            // The Bug-2 facility-refund leak: running 415466 > live 66386, no time-travel
            // signal -> cap the target DOWN to live so the patch cannot refund the spend.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 415466.0, runningBalance: 415466.0, currentLive: 66386.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.True(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Down, direction);
            Assert.Equal(66386.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_RunningAboveLive_Authoritative_ReturnsTargetUnchanged()
        {
            // Same numbers but an authoritative signal is set -> a genuine time-travel
            // restore that raises funds must apply unchanged (no DOWN clamp).
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 415466.0, runningBalance: 415466.0, currentLive: 66386.0,
                epsilon: 0.01, authoritativeReduction: true, resource: "funds",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.False(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, direction);
            Assert.Equal(415466.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_RunningAboveLive_TargetAlreadyBelowLive_DoesNotFlagNoOpClamp()
        {
            // running 415466 > live 66386 (uplift guardable), but the reservation-aware
            // target 50000 is already BELOW live -> min(target, live) = target, the value
            // is unchanged so the DOWN branch must NOT flag a misleading clamp (plan §2.4).
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 50000.0, runningBalance: 415466.0, currentLive: 66386.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.False(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, direction);
            Assert.Equal(50000.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_RunningBelowLive_NoSignal_DirectionIsUp()
        {
            // The existing UP clamp now reports direction=Up via the 2-out overload.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 60000.0, runningBalance: 60000.0, currentLive: 100000.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.True(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Up, direction);
            Assert.Equal(100000.0, result);
        }

        [Fact]
        public void Reservation_RunningEqualsLive_TargetBelowLive_NoUpliftClamp()
        {
            // Reservation case under the symmetric change: running 33000 == live 33000
            // (running within eps of live, so NOT a guardable uplift) and target 18000 is
            // below live (a committed future spend reserved). Neither guard fires; the
            // lower reservation target is written unchanged so overspend stays prevented.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 18000.0, runningBalance: 33000.0, currentLive: 33000.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.False(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, direction);
            Assert.Equal(18000.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_Science_RunningAboveLive_NoSignal_ClampsDownToLive()
        {
            // Science mirror: running 600 > live 200 with no signal -> cap DOWN to live.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 600.0, runningBalance: 600.0, currentLive: 200.0,
                epsilon: 0.001, authoritativeReduction: false, resource: "science",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.True(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Down, direction);
            Assert.Equal(200.0, result);
        }

        [Fact]
        public void ApplyDrawdownGuard_Reputation_RunningAboveLive_NoSignal_ClampsDownToLive()
        {
            // Reputation mirror (discriminator == target, no reservation): running 50 >
            // live 20 with no signal -> cap DOWN to live so an unmodeled rep loss is held.
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: 50.0, runningBalance: 50.0, currentLive: 20.0,
                epsilon: 0.01, authoritativeReduction: false, resource: "reputation",
                out bool clamped, out KspStatePatcher.ClampDirection direction);

            Assert.True(clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Down, direction);
            Assert.Equal(20.0, result);
        }

        // ================================================================
        // EmitDrawdownGuardClamp — WARN + session-latched toast
        // ================================================================

        [Fact]
        public void EmitDrawdownGuardClamp_Funds_WarnsWithNumbersAndToastsOnce()
        {
            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Funds", runningBalance: 60000.0, currentLive: 100000.0,
                wouldBeTarget: 60000.0, clampedTo: 100000.0,
                toastText: "Kept your earned funds",
                sessionToastLatch: ref latch, perSubjectScienceNote: false);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("GUARDED DRAWDOWN")
                && l.Contains("resource=Funds")
                && l.Contains("running=60000")
                && l.Contains("live=100000")
                && l.Contains("wouldBeTarget=60000")
                && l.Contains("earned value preserved"));

            Assert.Single(screenMessages);
            Assert.Equal("Kept your earned funds", screenMessages[0].text);
            Assert.True(latch);
        }

        [Fact]
        public void EmitDrawdownGuardClamp_Funds_Down_WarnsWithUpliftWordingAndToastsOnce()
        {
            // Bug 2 DOWN direction: the WARN must read "GUARDED UPLIFT clamped" with the
            // spent-value tail, and the dedicated uplift toast fires once.
            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Funds", runningBalance: 415466.0, currentLive: 66386.0,
                wouldBeTarget: 415466.0, clampedTo: 66386.0,
                toastText: "Held your funds at the spent value",
                sessionToastLatch: ref latch, perSubjectScienceNote: false,
                direction: KspStatePatcher.ClampDirection.Down);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("GUARDED UPLIFT clamped")
                && l.Contains("resource=Funds")
                && l.Contains("running=415466")
                && l.Contains("live=66386")
                && l.Contains("wouldBeTarget=415466")
                && l.Contains("clampedTo=66386")
                && l.Contains("spent value held"));
            // It must NOT use the drawdown wording.
            Assert.DoesNotContain(logLines, l => l.Contains("GUARDED DRAWDOWN"));

            Assert.Single(screenMessages);
            Assert.Equal("Held your funds at the spent value", screenMessages[0].text);
            Assert.True(latch);
        }

        [Fact]
        public void EmitDrawdownGuardClamp_Science_WarnNotesPerSubjectDivergence()
        {
            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Science", runningBalance: 100.0, currentLive: 500.0,
                wouldBeTarget: 100.0, clampedTo: 500.0,
                toastText: "Kept your earned science",
                sessionToastLatch: ref latch, perSubjectScienceNote: true);

            Assert.Contains(logLines, l =>
                l.Contains("GUARDED DRAWDOWN")
                && l.Contains("resource=Science")
                && l.Contains("per-subject"));
            Assert.Single(screenMessages);
            Assert.Equal("Kept your earned science", screenMessages[0].text);
        }

        [Fact]
        public void EmitDrawdownGuardClamp_Reputation_Toasts()
        {
            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Reputation", runningBalance: -30.0, currentLive: -10.0,
                wouldBeTarget: -30.0, clampedTo: -10.0,
                toastText: "Kept your earned reputation",
                sessionToastLatch: ref latch, perSubjectScienceNote: false);

            Assert.Single(screenMessages);
            Assert.Equal("Kept your earned reputation", screenMessages[0].text);
            Assert.Contains(logLines, l =>
                l.Contains("GUARDED DRAWDOWN") && l.Contains("resource=Reputation"));
        }

        [Fact]
        public void EmitDrawdownGuardClamp_SessionLatch_WarnsEveryTimeButToastsOnce()
        {
            bool latch = false;
            for (int i = 0; i < 3; i++)
            {
                KspStatePatcher.EmitDrawdownGuardClamp(
                    "Funds", runningBalance: 60000.0, currentLive: 100000.0,
                    wouldBeTarget: 60000.0, clampedTo: 100000.0,
                    toastText: "Kept your earned funds",
                    sessionToastLatch: ref latch, perSubjectScienceNote: false);
            }

            // The WARN documents the ongoing leak every guarded recalc...
            int warnCount = logLines.FindAll(l => l.Contains("GUARDED DRAWDOWN")).Count;
            Assert.Equal(3, warnCount);
            // ...but the toast fires exactly once per session.
            Assert.Single(screenMessages);
        }

        // ================================================================
        // Blocker 2 regression — reservation drawdown of the SPENDABLE target must
        // NOT be clamped (the running balance stays >= live).
        // ================================================================

        [Fact]
        public void Blocker2_ReservationLowersAvailableButNotRunning_GuardDoesNotClamp()
        {
            // A committed FUTURE FundsSpending reserves against the spendable amount, so
            // GetAvailableFunds() drops below GetRunningBalance() while the running balance
            // (the actual career total) stays intact. Live == the running balance here
            // (a normal load: KSP loads the singleton from the .sfs at the running value).
            var module = new FundsModule();
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 25000f },
                new GameAction { UT = 50, Type = GameActionType.FundsEarning, FundsAwarded = 8000f, FundsSource = FundsEarningSource.Milestone },
                // A FUTURE committed spend (UT=100, beyond the walked cutoff) -> reserved.
                new GameAction { UT = 100, Type = GameActionType.FundsSpending, FundsSpent = 15000f, FundsSpendingSource = FundsSpendingSource.VesselBuild, RecordingId = "rec-B" },
            };

            module.ComputeTotalSpendings(actions);
            module.ProcessAction(actions[0]); // seed 25000
            module.ProcessAction(actions[1]); // earn +8000

            double running = module.GetRunningBalance();   // 33000
            double available = module.GetAvailableFunds(); // 18000 (15000 future spend reserved)

            Assert.True(available < running,
                $"reservation must lower available ({available}) below running ({running})");

            // The live value equals the running balance on a normal load (no leak).
            double live = running;
            double result = KspStatePatcher.ApplyDrawdownGuard(
                patchTarget: available, runningBalance: running, currentLive: live,
                epsilon: 0.01, authoritativeReduction: false, resource: "funds",
                out bool clamped);

            // Guard must NOT fire: running >= live, so the reservation-aware target is
            // written unchanged and overspend prevention stays intact.
            Assert.False(clamped);
            Assert.Equal(available, result);
        }

        [Fact]
        public void ResetDrawdownGuardSessionLatches_ReArmsTheToast()
        {
            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Funds", 60000.0, 100000.0, 60000.0, 100000.0,
                "Kept your earned funds", ref latch, false);
            Assert.True(latch);
            Assert.Single(screenMessages);

            // A genuine new-session boundary re-arms the latch (a different save), so a
            // persistent leak toasts again. Drive a fresh latch local to mimic the
            // statics being cleared.
            KspStatePatcher.ResetDrawdownGuardSessionLatches();
            latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Funds", 60000.0, 100000.0, 60000.0, 100000.0,
                "Kept your earned funds", ref latch, false);
            Assert.Equal(2, screenMessages.Count);
        }
    }
}
