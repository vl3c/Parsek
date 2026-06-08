using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless coverage for the KspStatePatcher APPLY BOUNDARY (audit rec #5,
    /// docs/dev/ledger-state-reconstruction-audit.md §5.1 layer (e) / §5.3 risk 1-3).
    ///
    /// The live Patch* methods early-return on null KSP singletons under xUnit
    /// (SuppressUnityCallsForTesting), so the compute-delta -> clamp -> no-op / write
    /// composition used to be exercised only by 2 in-game tests with synthetic inputs.
    /// These tests drive the pure decision cores extracted from each Patch* wrapper
    /// (ResolveFundsPatch / ResolveSciencePoolPatch / ResolveReputationPatch /
    /// ResolveSubjectSciencePatch / ClassifyTechNodeForPatch / ResolveFacilityLevelPatch),
    /// which take the live value as a PARAMETER, so the full decision is testable without
    /// the game runtime. Each asserts the computed delta AND the resulting value AND, for
    /// the clamp sub-path, the WARN log line via the TestSinkForTesting seam.
    ///
    /// The thin Unity-call wrappers (AddFunds / AddScience / SetReputation / SetTechState /
    /// SetLevel + the read-back) stay covered live by RuntimeTests; this file covers every
    /// numeric/decision branch headlessly. The pre-existing IsGuardableDrawdown /
    /// ApplyDrawdownGuard / IsSuspiciousDrawdown / EmitDrawdownGuardClamp primitives are
    /// covered by DrawdownGuardTests / PatchFundsSanityTests; these tests cover their
    /// COMPOSITION into the per-resource patch decision.
    /// </summary>
    [Collection("Sequential")]
    public class KspStatePatcherApplyBoundaryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<(string text, float duration)> screenMessages =
            new List<(string, float)>();

        public KspStatePatcherApplyBoundaryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
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
        // ResolveFundsPatch — guard -> delta -> no-op / suspicious composition
        // ================================================================

        [Fact]
        public void ResolveFundsPatch_TargetMatchesLive_DoesNotWrite()
        {
            // Fails if the 0.01 no-op gate is dropped: the wrapper would call
            // AddFunds(0) and emit a spurious "old -> new" Info line on every recalc.
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 10000.0, targetFunds: 10000.0, runningFunds: 10000.0,
                authoritativeReduction: false);

            Assert.False(d.ShouldWrite);
            Assert.False(d.Clamped);
            Assert.False(d.SuspiciousDrawdown);
            Assert.Equal(0.0, d.Delta, 6);
            Assert.Equal(10000.0, d.EffectiveTarget, 6);
        }

        [Fact]
        public void ResolveFundsPatch_PlainCredit_WritesPositiveDelta()
        {
            // Fails if the delta sign/magnitude is wrong (current - target instead of
            // target - current), or if a legitimate earning is suppressed as a no-op.
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 1000.0, targetFunds: 1500.0, runningFunds: 1500.0,
                authoritativeReduction: false);

            Assert.True(d.ShouldWrite);
            Assert.False(d.Clamped);
            Assert.Equal(500.0, d.Delta, 6);
            Assert.Equal(1500.0, d.EffectiveTarget, 6);
            Assert.False(d.SuspiciousDrawdown); // a credit is never suspicious
        }

        [Fact]
        public void ResolveFundsPatch_ReservationLowersAvailableButNotRunning_DoesNotClamp()
        {
            // The §3.3 reservation case: a committed FUTURE spend lowers the available
            // target below live while the running balance stays intact (>= live). Fails if
            // the guard keys on the available target instead of the running balance, which
            // would wrongly clamp away legitimate overspend-prevention.
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 1000.0, targetFunds: 600.0, runningFunds: 1000.0,
                authoritativeReduction: false);

            Assert.False(d.Clamped);
            Assert.True(d.ShouldWrite);
            Assert.Equal(-400.0, d.Delta, 6);
            Assert.Equal(600.0, d.EffectiveTarget, 6);
        }

        [Fact]
        public void ResolveFundsPatch_MissingEarningLeak_ClampsToLiveAndCollapsesToNoOp()
        {
            // The BUG-A leak signature: the running balance itself dropped below live with
            // no time-travel authorization. The guard must raise the floor to live, which
            // makes the delta ~0 and collapses to a no-op (no downward write). Fails if the
            // guard floor isn't max(target, live) — the patch would silently write the
            // leaked-down value and corrupt the career.
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 1000.0, targetFunds: 600.0, runningFunds: 600.0,
                authoritativeReduction: false);

            Assert.True(d.Clamped);
            Assert.False(d.ShouldWrite);            // clamp -> noop collapse
            Assert.Equal(1000.0, d.EffectiveTarget, 6);
            Assert.Equal(0.0, d.Delta, 6);
            Assert.False(d.SuspiciousDrawdown);     // a no-op is never flagged suspicious
        }

        [Fact]
        public void ResolveFundsPatch_AuthorizedReduction_AppliesDrawdownUnchanged()
        {
            // A rewind/time-jump legitimately lowers the running balance. The authoritative
            // flag must bypass the clamp so the reduction applies. Fails if an authorized
            // rewind drawdown is wrongly clamped — rewinds would silently keep stale funds.
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 1000.0, targetFunds: 600.0, runningFunds: 600.0,
                authoritativeReduction: true);

            Assert.False(d.Clamped);
            Assert.True(d.ShouldWrite);
            Assert.Equal(-400.0, d.Delta, 6);
            Assert.Equal(600.0, d.EffectiveTarget, 6);
        }

        [Fact]
        public void ResolveFundsPatch_AuthorizedBigDrawdown_FlagsSuspiciousButStillWrites()
        {
            // >10% drop of a non-trivial pool. The suspicious flag is computed independently
            // of the clamp (it documents the shape of a missing-earnings bug even when the
            // reduction is authorized). Fails if SuspiciousDrawdown is gated on Clamped
            // rather than on (write && >10%), hiding the diagnostic on authorized paths.
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 10000.0, targetFunds: 8000.0, runningFunds: 8000.0,
                authoritativeReduction: true);

            Assert.True(d.ShouldWrite);
            Assert.False(d.Clamped);
            Assert.Equal(-2000.0, d.Delta, 6);
            Assert.True(d.SuspiciousDrawdown);
        }

        [Fact]
        public void ResolveFundsPatch_SmallDrawdown_NotFlaggedSuspicious()
        {
            // A 1% drawdown is normal churn, not a leak. Fails if the >10% threshold
            // regresses to flag every downward write (log spam + false alarms).
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 10000.0, targetFunds: 9900.0, runningFunds: 9900.0,
                authoritativeReduction: true);

            Assert.True(d.ShouldWrite);
            Assert.False(d.SuspiciousDrawdown);
            Assert.Equal(-100.0, d.Delta, 6);
        }

        [Fact]
        public void ResolveFundsPatch_LeakClamp_FeedsTheGuardWarnLogWithClampedValue()
        {
            // Drives the composed clamp sub-path exactly as PatchFunds does: resolve, then
            // (because Clamped) emit the guard WARN with the decision's EffectiveTarget as
            // clampedTo. Fails if the decision's EffectiveTarget does not flow into the WARN
            // the wrapper logs (the operator's only signal that a leak was caught).
            var d = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 100000.0, targetFunds: 60000.0, runningFunds: 60000.0,
                authoritativeReduction: false);
            Assert.True(d.Clamped);

            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Funds", runningBalance: 60000.0, currentLive: 100000.0,
                wouldBeTarget: 60000.0, clampedTo: d.EffectiveTarget,
                toastText: "Kept your earned funds",
                sessionToastLatch: ref latch, perSubjectScienceNote: false);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("GUARDED DRAWDOWN")
                && l.Contains("resource=Funds")
                && l.Contains("clampedTo=100000")
                && l.Contains("earned value preserved"));
            Assert.Single(screenMessages);
            Assert.Equal("Kept your earned funds", screenMessages[0].text);
        }

        // ================================================================
        // ResolveSciencePoolPatch — float-delta boundary + clamp composition
        // ================================================================

        [Fact]
        public void ResolveSciencePoolPatch_Credit_WritesFloatDelta()
        {
            // Fails if the double->float cast at the AddScience boundary is reordered or
            // lost (the delta is float; the target stays double for the logs).
            var d = KspStatePatcher.ResolveSciencePoolPatch(
                currentScience: 20f, targetScience: 50.0, runningScience: 50.0,
                authoritativeReduction: false);

            Assert.True(d.ShouldWrite);
            Assert.False(d.Clamped);
            Assert.Equal(30f, d.Delta);
            Assert.Equal(50.0, d.EffectiveTarget, 6);
        }

        [Fact]
        public void ResolveSciencePoolPatch_MissingEarningLeak_ClampsAndCollapses()
        {
            // Science uses the same guard with a 0.001 epsilon. running < live, no signal ->
            // clamp to live -> delta ~0 -> no-op. Fails if science skips the clamp (the
            // science pool would be driven below the earned value).
            var d = KspStatePatcher.ResolveSciencePoolPatch(
                currentScience: 50f, targetScience: 10.0, runningScience: 10.0,
                authoritativeReduction: false);

            Assert.True(d.Clamped);
            Assert.False(d.ShouldWrite);
            Assert.Equal(50.0, d.EffectiveTarget, 6);
            Assert.Equal(0f, d.Delta);
        }

        [Fact]
        public void ResolveSciencePoolPatch_Reservation_AppliesNegativeDeltaUnclamped()
        {
            // running >= live (intact) but the spendable target is below live -> a science
            // reservation, not a leak. Fails on the same available-vs-running confusion as
            // the funds reservation case but for the science pool.
            var d = KspStatePatcher.ResolveSciencePoolPatch(
                currentScience: 50f, targetScience: 30.0, runningScience: 50.0,
                authoritativeReduction: false);

            Assert.False(d.Clamped);
            Assert.True(d.ShouldWrite);
            Assert.Equal(-20f, d.Delta);
            Assert.Equal(30.0, d.EffectiveTarget, 6);
        }

        [Fact]
        public void ResolveSciencePoolPatch_LeakClamp_FeedsGuardWarnWithPerSubjectNote()
        {
            // The science clamp WARN carries the documented per-subject divergence note
            // (per-subject credited science is patched UNCLAMPED). Fails if the science
            // wrapper drops perSubjectScienceNote, hiding the Archive-divergence warning.
            var d = KspStatePatcher.ResolveSciencePoolPatch(
                currentScience: 500f, targetScience: 100.0, runningScience: 100.0,
                authoritativeReduction: false);
            Assert.True(d.Clamped);

            bool latch = false;
            KspStatePatcher.EmitDrawdownGuardClamp(
                "Science", runningBalance: 100.0, currentLive: 500.0,
                wouldBeTarget: 100.0, clampedTo: d.EffectiveTarget,
                toastText: "Kept your earned science",
                sessionToastLatch: ref latch, perSubjectScienceNote: true);

            Assert.Contains(logLines, l =>
                l.Contains("GUARDED DRAWDOWN")
                && l.Contains("resource=Science")
                && l.Contains("clampedTo=500")
                && l.Contains("per-subject"));
        }

        // ================================================================
        // ResolveReputationPatch — Set semantics (absolute, no delta)
        // ================================================================

        [Fact]
        public void ResolveReputationPatch_Increase_WritesAbsoluteTarget()
        {
            // Reputation uses SetReputation (absolute), so EffectiveTarget IS the value
            // written. Fails if rep accidentally computes an additive delta (the double-
            // curve bug the Set semantics exist to avoid).
            var d = KspStatePatcher.ResolveReputationPatch(
                currentRep: 100f, targetRep: 150f, authoritativeReduction: false);

            Assert.True(d.ShouldWrite);
            Assert.False(d.Clamped);
            Assert.Equal(150f, d.EffectiveTarget);
        }

        [Fact]
        public void ResolveReputationPatch_AuthorizedNegativePenalty_Applies()
        {
            // A legitimate (signal-set) reputation penalty into the negative range. Fails if
            // the magnitude-based downward test mishandles negative targets and clamps a
            // valid penalty back up.
            var d = KspStatePatcher.ResolveReputationPatch(
                currentRep: 50f, targetRep: -30f, authoritativeReduction: true);

            Assert.True(d.ShouldWrite);
            Assert.False(d.Clamped);
            Assert.Equal(-30f, d.EffectiveTarget);
        }

        [Fact]
        public void ResolveReputationPatch_LeakClamp_KeepsLiveAndCollapsesToNoOp()
        {
            // running == target below live, no signal -> clamp to live -> no-op. Fails if
            // rep's running==target wiring is broken (rep has no reservation, so the running
            // balance IS the target). EffectiveTargetRaw is the double fed to the WARN.
            var d = KspStatePatcher.ResolveReputationPatch(
                currentRep: 100f, targetRep: 40f, authoritativeReduction: false);

            Assert.True(d.Clamped);
            Assert.False(d.ShouldWrite);
            Assert.Equal(100f, d.EffectiveTarget);
            Assert.Equal(100.0, d.EffectiveTargetRaw, 6);
        }

        // ================================================================
        // ResolveSubjectSciencePatch — the UNCLAMPED per-subject path (audit gap 1)
        // ================================================================

        [Fact]
        public void ResolveSubjectSciencePatch_Drawdown_WritesTargetVerbatimUnclamped()
        {
            // THE highest-risk apply path: per-subject credited science is NOT covered by
            // the drawdown guard. A revert that lowers a subject's credited total must write
            // the lower value verbatim. Fails the instant anyone adds a max(current,target)
            // clamp here — that would freeze the Science Archive at stale totals.
            var d = KspStatePatcher.ResolveSubjectSciencePatch(
                currentScience: 124.8f, targetScience: 1.0f, scienceCap: 10f);

            Assert.True(d.ShouldWrite);
            Assert.Equal(1.0f, d.TargetScience);            // verbatim, NOT clamped to 124.8
            Assert.Equal(0.9f, d.ScientificValue, 0.0001f); // 1 - 1/10
        }

        [Fact]
        public void ResolveSubjectSciencePatch_ScientificValueFromCap()
        {
            // The diminishing-returns factor is 1 - target/cap. Fails if that formula
            // regresses (the Archive's remaining-value bar would render wrong).
            var d = KspStatePatcher.ResolveSubjectSciencePatch(
                currentScience: 0f, targetScience: 4.0f, scienceCap: 10f);

            Assert.True(d.ShouldWrite);
            Assert.Equal(0.6f, d.ScientificValue, 0.0001f); // 1 - 4/10
        }

        [Fact]
        public void ResolveSubjectSciencePatch_ZeroCap_ScientificValueZero()
        {
            // A zero scienceCap must short-circuit to 0, not divide by zero. Fails if the
            // cap>0 guard is dropped — NaN/Inf written into the Archive.
            var d = KspStatePatcher.ResolveSubjectSciencePatch(
                currentScience: 0f, targetScience: 4.0f, scienceCap: 0f);

            Assert.True(d.ShouldWrite);
            Assert.Equal(0f, d.ScientificValue);
        }

        [Fact]
        public void ResolveSubjectSciencePatch_OverCap_ScientificValueFlooredAtZero()
        {
            // target > cap would make 1 - target/cap negative; it must floor at 0. Fails if
            // the "< 0 -> 0" floor is lost (a negative scientificValue is invalid).
            var d = KspStatePatcher.ResolveSubjectSciencePatch(
                currentScience: 0f, targetScience: 15f, scienceCap: 10f);

            Assert.True(d.ShouldWrite);
            Assert.Equal(0f, d.ScientificValue);
        }

        [Fact]
        public void ResolveSubjectSciencePatch_WithinThreshold_DoesNotWrite()
        {
            // Below the 0.001 per-subject threshold is a no-op. Fails if the threshold
            // changes, causing churn writes + per-subject log spam on every recalc.
            var d = KspStatePatcher.ResolveSubjectSciencePatch(
                currentScience: 5.0f, targetScience: 5.0005f, scienceCap: 10f);

            Assert.False(d.ShouldWrite);
        }

        [Fact]
        public void ResolveSubjectSciencePatch_ClearedSubject_ZeroesCreditedTotal()
        {
            // A subject removed from the timeline targets 0. Fails if a removed subject is
            // not zeroed — stale Archive credit survives a revert.
            var d = KspStatePatcher.ResolveSubjectSciencePatch(
                currentScience: 8.0f, targetScience: 0f, scienceCap: 10f);

            Assert.True(d.ShouldWrite);
            Assert.Equal(0f, d.TargetScience);
            Assert.Equal(1.0f, d.ScientificValue, 0.0001f); // 1 - 0/10
        }

        // ================================================================
        // ClassifyTechNodeForPatch — node-set apply 2x2 (audit gap 2)
        // ================================================================

        [Fact]
        public void ClassifyTechNodeForPatch_ShouldBeAvailableButNotYet_MakesAvailable()
        {
            // A researched node missing from the live tree must be restored. Fails if a
            // post-rewind-forward node is left locked.
            Assert.Equal(KspStatePatcher.TechNodePatchAction.MakeAvailable,
                KspStatePatcher.ClassifyTechNodeForPatch(shouldBeAvailable: true, currentlyAvailable: false));
        }

        [Fact]
        public void ClassifyTechNodeForPatch_ShouldNotBeAvailableButIs_MakesUnavailable()
        {
            // The dangerous direction: a node not in the target set but currently researched
            // must be re-locked. Fails if a node that should be re-locked is left available
            // (or the count is silently masked — audit gap 2).
            Assert.Equal(KspStatePatcher.TechNodePatchAction.MakeUnavailable,
                KspStatePatcher.ClassifyTechNodeForPatch(shouldBeAvailable: false, currentlyAvailable: true));
        }

        [Fact]
        public void ClassifyTechNodeForPatch_NoOpDirections_AreNotChanges()
        {
            // Already-correct nodes must classify as no-ops so they are not miscounted as
            // changes. Fails if a steady-state node is reported as madeAvailable/Unavailable
            // (false "career changed" signal on every recalc).
            Assert.Equal(KspStatePatcher.TechNodePatchAction.AlreadyAvailable,
                KspStatePatcher.ClassifyTechNodeForPatch(shouldBeAvailable: true, currentlyAvailable: true));
            Assert.Equal(KspStatePatcher.TechNodePatchAction.AlreadyUnavailable,
                KspStatePatcher.ClassifyTechNodeForPatch(shouldBeAvailable: false, currentlyAvailable: false));
        }

        // ================================================================
        // ResolveFacilityLevelPatch — ledger-tier -> KSP-level map + gate (audit gap 4)
        // ================================================================

        [Fact]
        public void ResolveFacilityLevelPatch_Upgrade_MapsTierAndWrites()
        {
            // ledger tier 2 maps to KSP level 1. Fails if the ledger->KSP mapping is
            // bypassed (a tier-2 ledger written as KSP level 2 over-upgrades the facility).
            var d = FacilityStatePatcher.ResolveFacilityLevelPatch(currentKspLevel: 0, ledgerLevel: 2);

            Assert.True(d.ShouldWrite);
            Assert.Equal(1, d.TargetKspLevel);
        }

        [Fact]
        public void ResolveFacilityLevelPatch_AlreadyAtMappedLevel_DoesNotWrite()
        {
            // ledger tier 3 maps to KSP level 2; if the facility is already at KSP level 2
            // it is a no-op. Fails if the gate compares the ledger tier (3) to the KSP level
            // (2) directly — an off-by-one that re-sets the facility on every recalc.
            var d = FacilityStatePatcher.ResolveFacilityLevelPatch(currentKspLevel: 2, ledgerLevel: 3);

            Assert.False(d.ShouldWrite);
            Assert.Equal(2, d.TargetKspLevel);
        }

        [Fact]
        public void ResolveFacilityLevelPatch_Downgrade_MapsTierAndWrites()
        {
            // ledger tier 1 maps to KSP level 0. Fails if a silently-downgraded facility
            // (audit gap 4: a launchpad that blocks launches) is not applied.
            var d = FacilityStatePatcher.ResolveFacilityLevelPatch(currentKspLevel: 2, ledgerLevel: 1);

            Assert.True(d.ShouldWrite);
            Assert.Equal(0, d.TargetKspLevel);
        }
    }
}
