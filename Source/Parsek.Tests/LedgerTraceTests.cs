using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the <see cref="LedgerTrace"/> event-driven ledger-apply
    /// tracer: every pure Tier-C predicate (matching / diverging / boundary / NaN /
    /// Inf), the grep-stable Tier-A <c>FormatStructural</c> line, the three emit
    /// tiers' log-line schema and gating, the Tier-B change-only suppression, and
    /// the <c>ledgerTracing</c> persistence round-trip. Touches shared static state
    /// (LedgerTrace dict/seq/force-flag, ParsekLog sink, ParsekSettings override,
    /// the settings store), so it runs in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerTraceTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerTraceTests()
        {
            LedgerTrace.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerTrace.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Tier-C pure predicates
        // ================================================================

        // ---- IsResourceDrift ----

        [Fact]
        public void IsResourceDrift_WithinTolerance_False()
        {
            // diff 0.005 < tol 0.01 -> not a drift. Fails if the predicate flagged a
            // sub-tolerance jitter the patcher itself treats as a no-op.
            Assert.False(LedgerTrace.IsResourceDrift(100.0, 100.005, 0.01));
        }

        [Fact]
        public void IsResourceDrift_BeyondTolerance_True()
        {
            // diff 0.05 > tol 0.01 -> a real drift. Fails if a clear divergence is missed.
            Assert.True(LedgerTrace.IsResourceDrift(100.0, 100.05, 0.01));
        }

        [Fact]
        public void IsResourceDrift_ExactlyAtTolerance_False()
        {
            // |diff| == tol is WITHIN tolerance (strict > only). Fails if the boundary
            // is inclusive and a borderline jitter spuriously flags. Use target=0 so the
            // subtraction |0 - tol| == tol is exactly representable (no float epsilon).
            Assert.False(LedgerTrace.IsResourceDrift(0.0, 0.01, 0.01));
        }

        [Fact]
        public void IsResourceDrift_NaNActual_False()
        {
            // No trustworthy read-back -> do not flag. Fails if a NaN live read raises a
            // spurious anomaly.
            Assert.False(LedgerTrace.IsResourceDrift(100.0, double.NaN, 0.01));
        }

        [Fact]
        public void IsResourceDrift_InfinityActual_False()
        {
            Assert.False(LedgerTrace.IsResourceDrift(100.0, double.PositiveInfinity, 0.01));
            Assert.False(LedgerTrace.IsResourceDrift(100.0, double.NegativeInfinity, 0.01));
        }

        [Fact]
        public void IsResourceDrift_NaNTarget_True()
        {
            // A corrupt COMPUTED target IS the bug (RewindReadbackGuard semantics). Fails
            // if a NaN target is silently swallowed instead of flagged.
            Assert.True(LedgerTrace.IsResourceDrift(double.NaN, 100.0, 0.01));
        }

        [Fact]
        public void IsResourceDrift_InfinityTarget_True()
        {
            Assert.True(LedgerTrace.IsResourceDrift(double.PositiveInfinity, 100.0, 0.01));
            Assert.True(LedgerTrace.IsResourceDrift(double.NegativeInfinity, 100.0, 0.01));
        }

        // ---- IsSubjectScienceDrift (same contract as IsResourceDrift) ----

        [Fact]
        public void IsSubjectScienceDrift_WithinTolerance_False()
        {
            Assert.False(LedgerTrace.IsSubjectScienceDrift(5.0, 5.0005, 0.001));
        }

        [Fact]
        public void IsSubjectScienceDrift_BeyondTolerance_True()
        {
            Assert.True(LedgerTrace.IsSubjectScienceDrift(5.0, 5.01, 0.001));
        }

        [Fact]
        public void IsSubjectScienceDrift_ExactlyAtTolerance_False()
        {
            // target=0 so |0 - tol| == tol is exactly representable (no float epsilon).
            Assert.False(LedgerTrace.IsSubjectScienceDrift(0.0, 0.001, 0.001));
        }

        [Fact]
        public void IsSubjectScienceDrift_NaNActual_False()
        {
            Assert.False(LedgerTrace.IsSubjectScienceDrift(5.0, double.NaN, 0.001));
        }

        [Fact]
        public void IsSubjectScienceDrift_InfinityActual_False()
        {
            Assert.False(LedgerTrace.IsSubjectScienceDrift(5.0, double.PositiveInfinity, 0.001));
        }

        [Fact]
        public void IsSubjectScienceDrift_NaNTarget_True()
        {
            Assert.True(LedgerTrace.IsSubjectScienceDrift(double.NaN, 5.0, 0.001));
        }

        [Fact]
        public void IsSubjectScienceDrift_InfinityTarget_True()
        {
            Assert.True(LedgerTrace.IsSubjectScienceDrift(double.NegativeInfinity, 5.0, 0.001));
        }

        // ---- IsFacilityLevelMismatch ----

        [Fact]
        public void IsFacilityLevelMismatch_EqualLevels_False()
        {
            Assert.False(LedgerTrace.IsFacilityLevelMismatch(2, 2));
        }

        [Fact]
        public void IsFacilityLevelMismatch_DifferentLevels_True()
        {
            Assert.True(LedgerTrace.IsFacilityLevelMismatch(2, 1));
            Assert.True(LedgerTrace.IsFacilityLevelMismatch(0, 2));
        }

        // ---- IsTechNodePresenceMismatch ----

        [Fact]
        public void IsTechNodePresenceMismatch_AllCombos()
        {
            // Mismatch iff the two bools disagree. Fails if any combo is inverted.
            Assert.False(LedgerTrace.IsTechNodePresenceMismatch(true, true));
            Assert.False(LedgerTrace.IsTechNodePresenceMismatch(false, false));
            Assert.True(LedgerTrace.IsTechNodePresenceMismatch(true, false));
            Assert.True(LedgerTrace.IsTechNodePresenceMismatch(false, true));
        }

        // ---- IsContractPresenceMismatch ----

        [Fact]
        public void IsContractPresenceMismatch_AllCombos()
        {
            Assert.False(LedgerTrace.IsContractPresenceMismatch(true, true));
            Assert.False(LedgerTrace.IsContractPresenceMismatch(false, false));
            Assert.True(LedgerTrace.IsContractPresenceMismatch(true, false));
            Assert.True(LedgerTrace.IsContractPresenceMismatch(false, true));
        }

        // ---- ResourceTolerance ----

        [Fact]
        public void ResourceTolerance_MatchesPatcherMagnitudes()
        {
            // The read-back tolerances must equal the KspStatePatcher Patch* no-op-guard
            // deltas (funds 0.01, science 0.001, reputation 0.01) so a drift the patcher
            // treats as "no change" is never flagged. Fails if a magnitude drifts.
            Assert.Equal(0.01, LedgerTrace.ResourceTolerance("funds"));
            Assert.Equal(0.001, LedgerTrace.ResourceTolerance("science"));
            Assert.Equal(0.001, LedgerTrace.ResourceTolerance("subject-science"));
            Assert.Equal(0.01, LedgerTrace.ResourceTolerance("reputation"));
            // Unknown resource falls back to the loosest (0.01).
            Assert.Equal(0.01, LedgerTrace.ResourceTolerance("mystery"));
        }

        // ================================================================
        // Tier-A FormatStructural (pure, grep-stable)
        // ================================================================

        [Fact]
        public void FormatStructural_FieldOrderIsStableAndComplete()
        {
            var snap = new LedgerTrace.LedgerStructuralSnapshot
            {
                Funds = 39700.0,
                Science = 50.5,
                Reputation = 12.25,
                Facilities = "LaunchPad:2,Runway:1",
                TargetTechNodes = 7,
                Contracts = 3,
                Cutoff = 12345.5,
                AuthoritativeReduction = true
            };

            string line = LedgerTrace.FormatStructural(42, snap);

            // Exact field order is load-bearing for downstream greps.
            int iPhase = line.IndexOf("phase=Structural");
            int iSeq = line.IndexOf("recalcSeq=42");
            int iFunds = line.IndexOf("funds=39700.00");
            int iScience = line.IndexOf("science=50.50");
            int iRep = line.IndexOf("rep=12.25");
            int iFac = line.IndexOf("facilities=[LaunchPad:2,Runway:1]");
            int iTech = line.IndexOf("targetTechNodes=7");
            int iContracts = line.IndexOf("contracts=3");
            int iCutoff = line.IndexOf("cutoff=12345.5");
            int iAuth = line.IndexOf("authReduction=true");

            Assert.True(iPhase >= 0 && iSeq > iPhase && iFunds > iSeq && iScience > iFunds
                && iRep > iScience && iFac > iRep && iTech > iFac && iContracts > iTech
                && iCutoff > iContracts && iAuth > iCutoff,
                "FormatStructural field order broke: " + line);
        }

        [Fact]
        public void FormatStructural_NullCutoff_RendersNullToken()
        {
            var snap = new LedgerTrace.LedgerStructuralSnapshot { Cutoff = null };
            string line = LedgerTrace.FormatStructural(1, snap);
            Assert.Contains("cutoff=null", line);
        }

        [Fact]
        public void FormatStructural_ValuedCutoff_RendersInvariantValue()
        {
            var snap = new LedgerTrace.LedgerStructuralSnapshot { Cutoff = 9876.5 };
            string line = LedgerTrace.FormatStructural(1, snap);
            Assert.Contains("cutoff=9876.5", line);
            Assert.DoesNotContain("cutoff=null", line);
        }

        [Fact]
        public void FormatStructural_AuthReductionFalse_RendersFalse()
        {
            var snap = new LedgerTrace.LedgerStructuralSnapshot { AuthoritativeReduction = false };
            string line = LedgerTrace.FormatStructural(1, snap);
            Assert.Contains("authReduction=false", line);
        }

        [Fact]
        public void FormatStructural_NaNNumerics_RenderSafely()
        {
            // NaN/Inf pool values must render as the safe tokens, never "NaN"-crash a parse.
            var snap = new LedgerTrace.LedgerStructuralSnapshot
            {
                Funds = double.NaN,
                Science = double.PositiveInfinity,
                Reputation = double.NegativeInfinity
            };
            string line = LedgerTrace.FormatStructural(1, snap);
            Assert.Contains("funds=NaN", line);
            Assert.Contains("science=Infinity", line);
            Assert.Contains("rep=-Infinity", line);
        }

        [Fact]
        public void FormatStructural_CarriesRecalcSeq()
        {
            var snap = new LedgerTrace.LedgerStructuralSnapshot();
            Assert.Contains("recalcSeq=99", LedgerTrace.FormatStructural(99, snap));
        }

        // ================================================================
        // Tier-A EmitStructural (log-assertion + gating)
        // ================================================================

        [Fact]
        public void EmitStructural_Enabled_EmitsOneInfoStructuralLine()
        {
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.EmitStructural(new LedgerTrace.LedgerStructuralSnapshot
            {
                Funds = 100.0,
                Science = 10.0,
                Reputation = 1.0,
                Facilities = "VAB:1",
                TargetTechNodes = 2,
                Contracts = 1,
                Cutoff = null,
                AuthoritativeReduction = false
            });

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[LedgerTrace]")
                && l.Contains("phase=Structural")
                && l.Contains("funds=100.00")
                && l.Contains("facilities=[VAB:1]"));
        }

        [Fact]
        public void EmitStructural_Disabled_NoOp()
        {
            LedgerTrace.ForceEnabledForTesting = false;
            LedgerTrace.EmitStructural(new LedgerTrace.LedgerStructuralSnapshot { Funds = 1.0 });
            Assert.Empty(logLines);
        }

        [Fact]
        public void EmitStructural_UsesCurrentRecalcSeq()
        {
            LedgerTrace.ForceEnabledForTesting = true;
            LedgerTrace.BeginRecalc(); // seq -> 1
            LedgerTrace.BeginRecalc(); // seq -> 2

            LedgerTrace.EmitStructural(new LedgerTrace.LedgerStructuralSnapshot());

            Assert.Equal(2, LedgerTrace.CurrentRecalcSeqForTesting);
            Assert.Contains(logLines, l => l.Contains("phase=Structural") && l.Contains("recalcSeq=2"));
        }

        // ================================================================
        // Tier-B EmitOnChange (change-only suppression + gating)
        // ================================================================

        [Fact]
        public void EmitOnChange_Disabled_NoOp()
        {
            LedgerTrace.ForceEnabledForTesting = false;
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            Assert.Empty(logLines);
        }

        [Fact]
        public void EmitOnChange_FirstValue_Emits()
        {
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[LedgerTrace]")
                && l.Contains("phase=Change")
                && l.Contains("resource=facility")
                && l.Contains("id=LaunchPad")
                && l.Contains("change=0->1"));
        }

        [Fact]
        public void EmitOnChange_SameValueTwice_EmitsOnce()
        {
            // Steady state is silent: a repeat of identical change details for the same
            // key suppresses. Fails if the owned dict does not gate the second call.
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            int afterFirst = logLines.Count;
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");

            Assert.Equal(afterFirst, logLines.Count);
            Assert.Equal(1, logLines.Count(l => l.Contains("id=LaunchPad")));
        }

        [Fact]
        public void EmitOnChange_NewValue_EmitsFreshLine()
        {
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "1->2");

            Assert.Equal(2, logLines.Count(l => l.Contains("id=LaunchPad")));
            Assert.Contains(logLines, l => l.Contains("change=1->2"));
        }

        [Fact]
        public void EmitOnChange_DifferentKeysIndependent()
        {
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            LedgerTrace.EmitOnChange("tech-node", "LaunchPad", "0->1");

            // Same id but different resource -> distinct keys -> both emit.
            Assert.Contains(logLines, l => l.Contains("resource=facility") && l.Contains("id=LaunchPad"));
            Assert.Contains(logLines, l => l.Contains("resource=tech-node") && l.Contains("id=LaunchPad"));
        }

        [Fact]
        public void BeginRecalc_ClearsSteadyStateGuard_SoSameValueReEmits()
        {
            // A new recalc burst must re-emit the same change (the steady-state dict is
            // per-recalc). Fails if BeginRecalc leaves the dict populated.
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.BeginRecalc();
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            int afterFirstRecalc = logLines.Count;

            LedgerTrace.BeginRecalc();
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");

            Assert.True(logLines.Count > afterFirstRecalc);
        }

        // ================================================================
        // Tier-C EmitAnomaly (log-assertion + gating)
        // ================================================================

        [Fact]
        public void EmitAnomaly_Disabled_NoOp()
        {
            LedgerTrace.ForceEnabledForTesting = false;
            LedgerTrace.EmitAnomaly("funds", "pool", "ledger-vs-truth", "target=1 actual=2");
            Assert.Empty(logLines);
        }

        [Fact]
        public void EmitAnomaly_Enabled_EmitsOneWarnLedgerVsTruthLine()
        {
            LedgerTrace.ForceEnabledForTesting = true;

            LedgerTrace.EmitAnomaly("funds", "pool", "ledger-vs-truth",
                "target=39700.000 actual=39600.000 delta=100.000 tol=0.01");

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[LedgerTrace]")
                && l.Contains("phase=Anomaly")
                && l.Contains("reason=ledger-vs-truth")
                && l.Contains("resource=funds")
                && l.Contains("id=pool")
                && l.Contains("target=39700.000")
                && l.Contains("delta=100.000"));
        }

        [Fact]
        public void EmitAnomaly_CarriesRecalcSeq()
        {
            LedgerTrace.ForceEnabledForTesting = true;
            LedgerTrace.BeginRecalc(); // seq 1
            LedgerTrace.EmitAnomaly("science", "pool", "ledger-vs-truth", "target=1 actual=2");
            Assert.Contains(logLines, l => l.Contains("phase=Anomaly") && l.Contains("recalcSeq=1"));
        }

        // ================================================================
        // GATING: nothing leaks when disabled through either gate path
        // ================================================================

        [Fact]
        public void AllEmits_DisabledViaForceFlagAndSetting_ProduceZeroLines()
        {
            // Both gate inputs false: ForceEnabledForTesting=false AND a settings
            // override with ledgerTracing=false. Every entry point must be silent and
            // must not advance the owned sequence. Fails if any emit or BeginRecalc
            // leaks a line or bumps the seq while fully disabled.
            LedgerTrace.ForceEnabledForTesting = false;
            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { ledgerTracing = false };

            LedgerTrace.BeginRecalc();
            LedgerTrace.EmitStructural(new LedgerTrace.LedgerStructuralSnapshot { Funds = 1.0 });
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            LedgerTrace.EmitAnomaly("funds", "pool", "ledger-vs-truth", "x");

            Assert.Empty(logLines);
            Assert.Equal(0, LedgerTrace.CurrentRecalcSeqForTesting);
        }

        [Fact]
        public void AllEmits_EnabledViaSetting_ResumeEmission()
        {
            // Flip the setting on (no force flag) -> IsEnabled follows ParsekSettings.Current.
            LedgerTrace.ForceEnabledForTesting = false;
            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { ledgerTracing = true };

            LedgerTrace.BeginRecalc();
            LedgerTrace.EmitStructural(new LedgerTrace.LedgerStructuralSnapshot { Funds = 1.0 });
            LedgerTrace.EmitOnChange("facility", "LaunchPad", "0->1");
            LedgerTrace.EmitAnomaly("funds", "pool", "ledger-vs-truth", "x");

            Assert.Equal(1, LedgerTrace.CurrentRecalcSeqForTesting);
            Assert.Contains(logLines, l => l.Contains("phase=Structural"));
            Assert.Contains(logLines, l => l.Contains("phase=Change"));
            Assert.Contains(logLines, l => l.Contains("phase=Anomaly"));
        }

        [Fact]
        public void IsEnabled_FollowsParsekSettingsCurrent()
        {
            LedgerTrace.ForceEnabledForTesting = false;

            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { ledgerTracing = false };
            Assert.False(LedgerTrace.IsEnabled);

            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { ledgerTracing = true };
            Assert.True(LedgerTrace.IsEnabled);
        }

        // ================================================================
        // ledgerTracing persistence round-trip
        // ================================================================

        [Fact]
        public void GetStoredLedgerTracing_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredLedgerTracing());
        }

        [Fact]
        public void SetStoredLedgerTracing_RoundTrips()
        {
            ParsekSettingsPersistence.SetStoredLedgerTracingForTesting(true);
            Assert.True(ParsekSettingsPersistence.GetStoredLedgerTracing().Value);
        }

        [Fact]
        public void RecordLedgerTracing_UpdatesInMemoryStore()
        {
            ParsekSettingsPersistence.RecordLedgerTracing(true);
            Assert.True(ParsekSettingsPersistence.GetStoredLedgerTracing().Value);
        }

        [Fact]
        public void ResetForTesting_ClearsStoredLedgerTracing()
        {
            ParsekSettingsPersistence.SetStoredLedgerTracingForTesting(true);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredLedgerTracing());
        }

        [Fact]
        public void ApplyTo_RestoresStoredLedgerTracing()
        {
            // The persistence layer is the OnSave/OnLoad durability path: a stored true
            // must win over a settings instance that loaded false, and log the restore.
            ParsekSettingsPersistence.SetStoredLedgerTracingForTesting(true);
            var settings = new ParsekSettings { ledgerTracing = false };

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.True(settings.ledgerTracing);
            Assert.Contains(logLines, l =>
                l.Contains("[SettingsStore]")
                && l.Contains("Restored ledgerTracing")
                && l.Contains("False -> True"));
        }
    }
}
