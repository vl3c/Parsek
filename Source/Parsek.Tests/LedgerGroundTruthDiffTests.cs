using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="LedgerGroundTruthDiff"/> (Layer A of the ledger
    /// ground-truth harness). Builds hand-constructed snapshots and asserts the
    /// per-facet policy: seeded pools HARD, per-identity facets + phantoms
    /// report-only by default (promoted under StrictPerIdentityForTesting),
    /// recovery consistency HARD only when guid-corroborated.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerGroundTruthDiffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerGroundTruthDiffTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = false;
        }

        public void Dispose()
        {
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Snapshot builders
        // ================================================================

        /// <summary>A healthy save with funds/science/rep all present.</summary>
        private static CareerSaveSnapshot HealthySave()
        {
            return new CareerSaveSnapshot
            {
                Parsed = true,
                HasFunds = true,
                Funds = 50000.0,
                HasScience = true,
                SciencePool = 200.0,
                HasRep = true,
                Reputation = 75.0
            };
        }

        /// <summary>A reconstruction matching <see cref="HealthySave"/> exactly.</summary>
        private static LedgerReconstructionSnapshot HealthyRecon()
        {
            return new LedgerReconstructionSnapshot
            {
                HasFunds = true,
                Funds = 50000.0,
                HasScience = true,
                SciencePool = 200.0,
                HasRep = true,
                Reputation = 75.0
            };
        }

        private static IReadOnlyDictionary<string, int> NoMaxLevels()
        {
            return new Dictionary<string, int>();
        }

        // ================================================================
        // Diff tests
        // ================================================================

        [Fact]
        public void Diff_HealthyMatch_EmptyReport()
        {
            // Guards: a clean save+recon emitting a divergence.
            var report = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());

            Assert.Empty(report.All);
            Assert.Empty(report.HardFailures(strict: false));
            Assert.True(report.FacetsCompared >= 3); // funds + science + rep at minimum
            Assert.Contains(logLines, l => l.Contains("[LedgerGroundTruth]") && l.Contains("Compare: result"));
        }

        [Fact]
        public void Diff_FundsBeyondTolerance_HardFail()
        {
            // Guards: a real pool gap not flagged.
            var save = HealthySave();
            var recon = HealthyRecon();
            recon.Funds = 40000.0; // 10000 gap, tol 1.0

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d => d.Facet == DivergenceFacet.Funds);
            var hard = report.HardFailures(strict: false);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.Funds);
        }

        [Fact]
        public void Diff_WithinTolerance_NoDivergence()
        {
            // Guards: tolerance not honored.
            var save = HealthySave();
            var recon = HealthyRecon();
            recon.Funds = 50000.0 + 0.5;   // within funds tol 1.0
            recon.SciencePool = 200.0 + 0.05; // within science tol 0.1
            recon.Reputation = 75.0 + 0.05;   // within rep tol 0.1

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Funds);
            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.SciencePool);
            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Reputation);
        }

        // ================================================================
        // Seeded-pool uplift-clamp demotion (demote-on-uplift policy)
        // ================================================================

        [Fact]
        public void Diff_SeededPoolUpliftWithoutAuthoritative_ReportOnly()
        {
            // recon ABOVE save, no authoritative time-travel context => the production
            // drawdown guard would UPLIFT-clamp the patch DOWN to live, so the divergence
            // is the expected missing-channel surplus: UpliftClampedExpected, report-only
            // for ALL THREE seeded pools.
            var save = HealthySave();      // funds 50000 / sci 200 / rep 75
            var recon = HealthyRecon();
            recon.Funds = 50123.45;        // +123.45 above save (funds tol 1.0)
            recon.SciencePool = 290.0;     // +90 above save (science tol 0.1)
            recon.Reputation = 80.0;       // +5 above save (rep tol 0.1)

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels(),
                authoritativeReduction: false);

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Funds && d.Kind == DivergenceKind.UpliftClampedExpected);
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.SciencePool && d.Kind == DivergenceKind.UpliftClampedExpected);
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Reputation && d.Kind == DivergenceKind.UpliftClampedExpected);

            // None of them are hard by default (the whole point of demote-on-uplift).
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_SeededPoolDownward_StaysHard()
        {
            // recon BELOW save: the guard does NOT raise live to meet a lower recon, so
            // this is the real save-corruption class and stays a HARD ValueMismatch for
            // all three pools -- regardless of the authoritativeReduction flag.
            var save = HealthySave();
            var recon = HealthyRecon();
            recon.Funds = 40000.0;     // 10000 below save
            recon.SciencePool = 100.0; // 100 below save
            recon.Reputation = 50.0;   // 25 below save

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels(),
                authoritativeReduction: false);

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Funds && d.Kind == DivergenceKind.ValueMismatch);
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.SciencePool && d.Kind == DivergenceKind.ValueMismatch);
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Reputation && d.Kind == DivergenceKind.ValueMismatch);

            var hard = report.HardFailures(strict: false);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.Funds);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.SciencePool);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.Reputation);
        }

        [Fact]
        public void Diff_SeededPoolUpliftWithAuthoritative_StaysHard()
        {
            // recon ABOVE save BUT an authoritative time-travel context is active: the
            // guard does NOT clamp (the reduction/restore is authorized), so an over-running
            // recon is a genuine post-restore mismatch and stays a HARD ValueMismatch.
            var save = HealthySave();
            var recon = HealthyRecon();
            recon.Funds = 50123.45;    // above save
            recon.SciencePool = 290.0; // above save
            recon.Reputation = 80.0;   // above save

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels(),
                authoritativeReduction: true);

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Funds && d.Kind == DivergenceKind.ValueMismatch);
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.SciencePool && d.Kind == DivergenceKind.ValueMismatch);
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Reputation && d.Kind == DivergenceKind.ValueMismatch);

            var hard = report.HardFailures(strict: false);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.Funds);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.SciencePool);
            Assert.Contains(hard, d => d.Facet == DivergenceFacet.Reputation);
        }

        [Fact]
        public void Diff_SeededPoolUplift_StrictPromotesToHard()
        {
            // Even when demoted to report-only, an UpliftClampedExpected divergence is
            // promoted to hard under StrictPerIdentityForTesting (a clean Parsek-only test
            // career flown entirely under tracking should have NO surplus).
            var save = HealthySave();
            var recon = HealthyRecon();
            recon.SciencePool = 290.0; // +90 above save, no authoritative context

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels(),
                authoritativeReduction: false);

            // Report-only by default...
            Assert.Empty(report.HardFailures(strict: false));
            // ...promoted under strict.
            Assert.Contains(report.HardFailures(strict: true), d =>
                d.Facet == DivergenceFacet.SciencePool
                && d.Kind == DivergenceKind.UpliftClampedExpected);
        }

        [Fact]
        public void Diff_PhantomSubject_PhantomInRecon()
        {
            // Guards: a recon-only subject not flagged phantom.
            var save = HealthySave();
            save.SubjectScience["known@Kerbin"] = 5.0;
            var recon = HealthyRecon();
            recon.SubjectScience["known@Kerbin"] = 5.0;
            recon.SubjectScience["phantom@Duna"] = 3.0; // not in save

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.SubjectScience
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "phantom@Duna");

            // Report-only by default: not in the hard set.
            Assert.DoesNotContain(report.HardFailures(strict: false),
                d => d.Facet == DivergenceFacet.SubjectScience);
        }

        [Fact]
        public void Diff_SharedSubjectMismatch_ReportOnly()
        {
            // Guards: a shared-identity mismatch hard-failing by default.
            var save = HealthySave();
            save.SubjectScience["shared@Kerbin"] = 5.0;
            var recon = HealthyRecon();
            recon.SubjectScience["shared@Kerbin"] = 9.0; // 4.0 gap, subject tol 0.1

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.SubjectScience
                && d.Kind == DivergenceKind.ValueMismatch
                && d.Identity == "shared@Kerbin");
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_StrictMode_PromotesReportOnly()
        {
            // Guards: the strict flag not promoting per-identity / phantom entries.
            var save = HealthySave();
            save.SubjectScience["shared@Kerbin"] = 5.0;
            var recon = HealthyRecon();
            recon.SubjectScience["shared@Kerbin"] = 9.0;
            recon.SubjectScience["phantom@Duna"] = 3.0;

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Empty(report.HardFailures(strict: false));
            var strictHard = report.HardFailures(strict: true);
            Assert.Contains(strictHard, d =>
                d.Facet == DivergenceFacet.SubjectScience && d.Identity == "shared@Kerbin");
            Assert.Contains(strictHard, d =>
                d.Facet == DivergenceFacet.SubjectScience && d.Identity == "phantom@Duna");
        }

        [Fact]
        public void Diff_FacilityFractionToInt_UsesMaxLevel()
        {
            // Guards: the maxLevel conversion wrong. saveFrac 0.5 with maxLevel0=2
            // -> saveLevel0=1. Recon ledger level 2 (1-based) -> 1 (0-based) =>
            // MATCH (no divergence). A recon ledger level 3 -> 2 (0-based) =>
            // mismatch.
            var save = HealthySave();
            save.FacilityLevelFrac["SpaceCenter/LaunchPad"] = 0.5; // -> level0 = round(0.5*2) = 1
            var maxLevels = new Dictionary<string, int> { ["SpaceCenter/LaunchPad"] = 2 };

            // Matching recon: ledger level 2 -> ToKspFacilityLevel(2) = 1.
            var reconMatch = HealthyRecon();
            reconMatch.FacilityLevel["SpaceCenter/LaunchPad"] = 2;
            var matchReport = LedgerGroundTruthDiff.Compare(
                save, reconMatch, FacetTolerances.Default, maxLevels);
            Assert.DoesNotContain(matchReport.All, d => d.Facet == DivergenceFacet.Facility);

            // Mismatching recon: ledger level 3 -> ToKspFacilityLevel(3) = 2.
            var reconMismatch = HealthyRecon();
            reconMismatch.FacilityLevel["SpaceCenter/LaunchPad"] = 3;
            var mismatchReport = LedgerGroundTruthDiff.Compare(
                save, reconMismatch, FacetTolerances.Default, maxLevels);
            Assert.Contains(mismatchReport.All, d =>
                d.Facet == DivergenceFacet.Facility
                && d.Kind == DivergenceKind.ValueMismatch
                && d.Identity == "SpaceCenter/LaunchPad");
            // Report-only by default.
            Assert.Empty(mismatchReport.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_RecoveryCreditWithPresentVessel_Consistency()
        {
            // Guards: a present-vessel recovery (guid-corroborated) not flagged HARD.
            var save = HealthySave();
            save.Vessels.Add(new SaveVessel
            {
                Pid = "guid-still-here",
                PersistentId = 777u,
                Name = "ShouldBeGone",
                ResourceTotals = new Dictionary<string, double>()
            });
            var recon = HealthyRecon();
            recon.RecoveryCredits.Add(new RecoveryCredit
            {
                RecordingId = "rec-1",
                VesselName = "ShouldBeGone",
                VesselGuid = "guid-still-here", // guid-corroborated
                VesselPid = 777u,
                Amount = 1234.0
            });

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Vessel && d.Kind == DivergenceKind.Consistency);
            // Guid-corroborated => HARD even when not strict.
            Assert.Contains(report.HardFailures(strict: false), d =>
                d.Facet == DivergenceFacet.Vessel && d.Kind == DivergenceKind.Consistency);
        }

        [Fact]
        public void Diff_RecoveryCreditPidOnly_ReportNotHard()
        {
            // Guards: a pid-only identity hard-failing (craft-baked-pid caveat).
            var save = HealthySave();
            save.Vessels.Add(new SaveVessel
            {
                Pid = "guid-different", // different guid -> NOT a guid match
                PersistentId = 888u,
                Name = "PidCollision",
                ResourceTotals = new Dictionary<string, double>()
            });
            var recon = HealthyRecon();
            recon.RecoveryCredits.Add(new RecoveryCredit
            {
                RecordingId = "rec-2",
                VesselName = "Recovered",
                VesselGuid = "guid-recovered", // does not match any save vessel
                VesselPid = 888u,              // pid collides
                Amount = 4321.0
            });

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Vessel
                && d.Kind == DivergenceKind.Consistency
                && d.Detail.Contains("guidCorroborated=false"));
            // pid-only must NOT be hard by default.
            Assert.Empty(report.HardFailures(strict: false));
            // Strict mode promotes it.
            Assert.Contains(report.HardFailures(strict: true), d =>
                d.Facet == DivergenceFacet.Vessel && d.Kind == DivergenceKind.Consistency);
        }

        [Fact]
        public void Diff_RecoveryCreditAbsentVessel_Consistent()
        {
            // A recovered vessel correctly absent from the save => no divergence.
            var save = HealthySave(); // no vessels
            var recon = HealthyRecon();
            recon.RecoveryCredits.Add(new RecoveryCredit
            {
                RecordingId = "rec-3",
                VesselName = "Gone",
                VesselGuid = "guid-gone",
                VesselPid = 999u,
                Amount = 100.0
            });

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Vessel);
        }

        [Fact]
        public void Diff_MissingFacet_Skipped()
        {
            // A facet the save lacks (save.HasFunds=false) is not compared even
            // when the recon has a value.
            var save = HealthySave();
            save.HasFunds = false;
            var recon = HealthyRecon();
            recon.Funds = 999999.0; // would be a huge gap if compared

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Funds);
            Assert.Contains(logLines, l => l.Contains("CompareFunds") && l.Contains("skip"));
        }

        // ================================================================
        // Contract facet
        // ================================================================

        [Fact]
        public void Diff_ContractPhantom_ReportOnly()
        {
            // Recon thinks a contract is active that the save has never heard of
            // (absent from ContractGuidsAllStates) => PhantomInRecon, report-only.
            var save = HealthySave();
            save.ContractGuidsAllStates.Add("guid-known");
            save.ActiveContractGuids.Add("guid-known");
            var recon = HealthyRecon();
            recon.ActiveContractGuids.Add("guid-known");   // agrees -> no divergence
            recon.ActiveContractGuids.Add("guid-phantom"); // not in save at all

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Contract
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "guid-phantom");
            // The agreeing guid must NOT produce any contract divergence.
            Assert.DoesNotContain(report.All, d =>
                d.Facet == DivergenceFacet.Contract && d.Identity == "guid-known");

            // Report-only by default.
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_ContractActiveMissingInRecon_ReportOnly()
        {
            // The save lists a contract as Active but the recon does not consider
            // it active => MissingInRecon, report-only.
            var save = HealthySave();
            save.ContractGuidsAllStates.Add("guid-save-active");
            save.ActiveContractGuids.Add("guid-save-active");
            var recon = HealthyRecon();
            // recon has no active contracts.

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Contract
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "guid-save-active");

            // Report-only by default.
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_ContractActiveButNonActiveInSave_ValueMismatch()
        {
            // Recon thinks a guid is active; the save knows the guid (it is in
            // ContractGuidsAllStates) but it is NOT Active (e.g. Completed) =>
            // ValueMismatch, report-only. Mirrors the benign state-transition the
            // recon may not have captured.
            var save = HealthySave();
            save.ContractGuidsAllStates.Add("guid-completed"); // known...
            // ...but NOT in ActiveContractGuids (it is non-Active in the save).
            var recon = HealthyRecon();
            recon.ActiveContractGuids.Add("guid-completed");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Contract
                && d.Kind == DivergenceKind.ValueMismatch
                && d.Identity == "guid-completed");
            // It must NOT be flagged phantom (it IS in the all-states set).
            Assert.DoesNotContain(report.All, d =>
                d.Facet == DivergenceFacet.Contract
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "guid-completed");

            // Report-only by default.
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_ContractStrictMode_PromotesReportOnly()
        {
            // The strict flag promotes contract phantom + missing entries to hard.
            var save = HealthySave();
            save.ContractGuidsAllStates.Add("guid-save-active");
            save.ActiveContractGuids.Add("guid-save-active"); // active in save, not in recon -> missing
            var recon = HealthyRecon();
            recon.ActiveContractGuids.Add("guid-phantom");    // not in save -> phantom

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Empty(report.HardFailures(strict: false));
            var strictHard = report.HardFailures(strict: true);
            Assert.Contains(strictHard, d =>
                d.Facet == DivergenceFacet.Contract
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "guid-phantom");
            Assert.Contains(strictHard, d =>
                d.Facet == DivergenceFacet.Contract
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "guid-save-active");
        }

        // ================================================================
        // Milestone facet
        // ================================================================

        [Fact]
        public void Diff_MilestonePhantom_ReportOnly()
        {
            // Recon credits a milestone id the save has never recorded (neither
            // qualified nor bare form present) => PhantomInRecon, report-only.
            // Also verifies a recon id matching the BARE form of a qualified save
            // id is NOT flagged phantom (the parser emits both forms, so a single
            // Contains covers both).
            var save = HealthySave();
            // Save recorded a qualified milestone AND its bare form (as the parser
            // does); e.g. "Mun/Landing" plus bare "Landing".
            save.AllMilestoneIds.Add("Mun/Landing");
            save.AllMilestoneIds.Add("Landing");
            var recon = HealthyRecon();
            recon.CreditedMilestoneIds.Add("Landing");        // BARE form of a known id -> NOT phantom
            recon.CreditedMilestoneIds.Add("Duna/FirstFlag"); // truly unknown -> phantom

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "Duna/FirstFlag");
            // The bare-form match must NOT be flagged phantom.
            Assert.DoesNotContain(report.All, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "Landing");

            // Report-only by default.
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_MilestoneMissingInRecon_ReportOnly()
        {
            // The save completed a milestone the recon did not credit =>
            // MissingInRecon, report-only.
            var save = HealthySave();
            save.AllMilestoneIds.Add("Mun/Landing");
            save.CompletedMilestoneIds.Add("Mun/Landing");
            var recon = HealthyRecon();
            // recon credits nothing.

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "Mun/Landing");

            // Report-only by default.
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_MilestoneStrictMode_PromotesReportOnly()
        {
            // The strict flag promotes milestone phantom + missing entries to hard.
            var save = HealthySave();
            save.AllMilestoneIds.Add("Mun/Landing");
            save.CompletedMilestoneIds.Add("Mun/Landing"); // completed in save, not in recon -> missing
            var recon = HealthyRecon();
            recon.CreditedMilestoneIds.Add("Duna/Flyby");  // not in save -> phantom

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Empty(report.HardFailures(strict: false));
            var strictHard = report.HardFailures(strict: true);
            Assert.Contains(strictHard, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "Duna/Flyby");
            Assert.Contains(strictHard, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "Mun/Landing");
        }

        // ================================================================
        // A.5 - roster / tech / part-purchase / strategy facets (REPORT-ONLY)
        // ================================================================

        /// <summary>A save carrying one roster kerbal in the given state.</summary>
        private static CareerSaveSnapshot SaveWithRoster(params SaveKerbal[] kerbals)
        {
            var save = HealthySave();
            save.HasRoster = true;
            foreach (var k in kerbals)
                save.Roster.Add(k);
            return save;
        }

        private static SaveKerbal Kerbal(string name, string state, string type = "Crew")
        {
            return new SaveKerbal
            {
                Name = name,
                Gender = "Male",
                Type = type,
                Trait = "Pilot",
                State = state
            };
        }

        [Fact]
        public void Diff_RosterWithoutReconSurface_IsUncomparedNotDiffed()
        {
            // Guards: inventing a reconstruction. With no roster surface the facet
            // must emit NO divergence and must NOT count as a compared facet - the
            // save-side census goes to the log only.
            var save = SaveWithRoster(Kerbal("Bill Kerman", "Available"));
            var recon = HealthyRecon(); // HasRosterSurface stays false

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Roster);
            Assert.Equal(baseline.FacetsCompared, report.FacetsCompared);
            Assert.Contains(logLines, l => l.Contains("CompareRoster")
                && l.Contains("no reconstruction roster surface") && l.Contains("uncompared"));
        }

        [Fact]
        public void Diff_LedgerCreatedKerbalAbsentFromSaveRoster_PhantomReportOnly()
        {
            // Guards: a kerbal the ledger believes it created that the save roster
            // does not carry (the meaningful roster direction).
            var save = SaveWithRoster(Kerbal("Bill Kerman", "Available"));
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;
            recon.LedgerCreatedKerbals.Add("Phantomly Kerman");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Roster
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "Phantomly Kerman");
            Assert.Empty(report.HardFailures(strict: false));
            Assert.Contains(report.HardFailures(strict: true), d => d.Facet == DivergenceFacet.Roster);
        }

        [Fact]
        public void Diff_PermanentlyGoneKerbalStillAlive_ConsistencyReportOnly()
        {
            // Guards: the ledger holding a kerbal permanently gone (dead) while the
            // save still lists them alive. Report-only even though it is a
            // Consistency kind: only a GUID-CORROBORATED Vessel consistency entry is
            // always-hard, and a roster name is not a launch guid.
            var save = SaveWithRoster(Kerbal("Bill Kerman", "Available"));
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;
            recon.PermanentlyGoneKerbals.Add("Bill Kerman");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Roster
                && d.Kind == DivergenceKind.Consistency
                && d.Identity == "Bill Kerman"
                && d.Detail.Contains("state='Available'"));
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_PermanentlyGoneKerbalDeadOrAbsent_NoDivergence()
        {
            // Guards: false-positives on the two states that AGREE with the ledger -
            // dead in the roster, or absent from it entirely (which is exactly what
            // "gone" means after a dismissal).
            var save = SaveWithRoster(
                Kerbal("Bill Kerman", "Dead"),
                Kerbal("Bob Kerman", "Missing"));
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;
            recon.PermanentlyGoneKerbals.Add("Bill Kerman");
            recon.PermanentlyGoneKerbals.Add("Bob Kerman");
            recon.PermanentlyGoneKerbals.Add("Gone Kerman"); // not in the roster at all

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Roster);
        }

        [Fact]
        public void Diff_SaveKerbalsTheLedgerNeverMentions_AreNotDivergences()
        {
            // Guards: turning the delta-only ledger into a full-roster claim. The
            // stock four are legitimately unmentioned by the ledger.
            var save = SaveWithRoster(
                Kerbal("Jebediah Kerman", "Available"),
                Kerbal("Bill Kerman", "Available"),
                Kerbal("Bob Kerman", "Available"),
                Kerbal("Valentina Kerman", "Available"));
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Roster);
        }

        [Fact]
        public void Diff_TechNodeClaimedByReconButNotUnlockedInSave_PhantomReportOnly()
        {
            var save = HealthySave();
            save.HasTechTree = true;
            save.UnlockedTechIds.Add("start");
            var recon = HealthyRecon();
            recon.HasTechSurface = true;
            recon.ResearchedTechIds.Add("start");
            recon.ResearchedTechIds.Add("heavyRocketry"); // claimed, never unlocked

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.TechNode
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "heavyRocketry");
            Assert.Empty(report.HardFailures(strict: false));
            Assert.Contains(report.HardFailures(strict: true), d => d.Facet == DivergenceFacet.TechNode);
        }

        [Fact]
        public void Diff_TechNodeUnlockedInSaveButNotClaimed_IsCountedNotEmitted()
        {
            // Guards: emitting the delta-vs-absolute direction. The ledger's tech
            // surface is delta-only, so a pre-Parsek unlock is EXPECTED and must be
            // logged as a count, never as one divergence per node.
            var save = HealthySave();
            save.HasTechTree = true;
            save.UnlockedTechIds.Add("start");
            save.UnlockedTechIds.Add("basicRocketry");
            save.UnlockedTechIds.Add("stability");
            var recon = HealthyRecon();
            recon.HasTechSurface = true;
            recon.ResearchedTechIds.Add("stability");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.TechNode);
            Assert.Contains(logLines, l => l.Contains("CompareTechNodes")
                && l.Contains("unlockedNotClaimedByRecon=2"));
        }

        [Fact]
        public void Diff_TechWithoutReconSurface_IsUncompared()
        {
            var save = HealthySave();
            save.HasTechTree = true;
            save.UnlockedTechIds.Add("start");
            var recon = HealthyRecon(); // HasTechSurface stays false

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.TechNode);
            Assert.Equal(baseline.FacetsCompared, report.FacetsCompared);
            Assert.Contains(logLines, l => l.Contains("CompareTechNodes")
                && l.Contains("no reconstruction tech surface"));
        }

        [Fact]
        public void Diff_PartPurchases_SaveSideCensusOnlyWithoutASurface()
        {
            // Guards: fabricating a purchased-part reconstruction. Today NOTHING
            // declares that surface, so the facet reports and stays uncompared.
            var save = HealthySave();
            save.HasTechTree = true;
            save.UnlockedTechIds.Add("start");
            save.PurchasedPartNames.Add("mk1pod.v2");
            save.TechNodePartCounts["start"] = 1;
            var recon = HealthyRecon();

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.PartPurchase);
            Assert.Contains(logLines, l => l.Contains("ComparePartPurchases")
                && l.Contains("no reconstruction part-purchase surface")
                && l.Contains("saveParts=1"));
        }

        [Fact]
        public void Diff_PartPurchases_DiffWhenASurfaceIsDeclared()
        {
            // Guards: the forward-compatible branch. If a reconstruction ever DOES
            // declare purchased parts, a claim the save does not carry is a phantom.
            var save = HealthySave();
            save.HasTechTree = true;
            save.PurchasedPartNames.Add("mk1pod.v2");
            var recon = HealthyRecon();
            recon.HasPartPurchaseSurface = true;
            recon.PurchasedPartNames.Add("mk1pod.v2");
            recon.PurchasedPartNames.Add("neverBoughtPart");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.PartPurchase
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "neverBoughtPart");
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_Strategies_SaveSideCensusOnlyWithoutASurface()
        {
            // Guards: the SHAPE-ONLY contract. StrategiesModule exposes no active
            // set, so the facet reports the save side and compares nothing.
            var save = HealthySave();
            save.HasStrategySystem = true;
            save.Strategies.Add(new SaveStrategy
            {
                Name = "PatentsLicensingCfg",
                IsActive = true,
                ActivatedUT = 17022.76,
                Factor = 0.05
            });
            save.ActiveStrategyIds.Add("PatentsLicensingCfg");
            var recon = HealthyRecon();

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Strategy);
            Assert.Contains(logLines, l => l.Contains("CompareStrategies")
                && l.Contains("no reconstruction strategy surface")
                && l.Contains("saveActive=1"));
        }

        [Fact]
        public void Diff_Strategies_BothDirectionsWhenASurfaceIsDeclared()
        {
            // Guards: the forward-compatible branch. A strategy set is small and
            // ABSOLUTE on both sides, so neither direction is structural noise.
            var save = HealthySave();
            save.HasStrategySystem = true;
            save.ActiveStrategyIds.Add("PatentsLicensingCfg");
            var recon = HealthyRecon();
            recon.HasStrategySurface = true;
            recon.ActiveStrategyIds.Add("ResearchTiming");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Strategy
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "ResearchTiming");
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Strategy
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "PatentsLicensingCfg");
            Assert.Empty(report.HardFailures(strict: false));
            Assert.Equal(2, report.HardFailures(strict: true).Count);
        }

        [Fact]
        public void Diff_NewFacets_NeverPromoteToAlwaysHard()
        {
            // Guards: a new facet accidentally joining the always-hard set. Only the
            // seeded pools and guid-corroborated vessel consistency may be hard
            // regardless of strictness.
            foreach (DivergenceFacet facet in new[]
            {
                DivergenceFacet.Roster, DivergenceFacet.TechNode,
                DivergenceFacet.PartPurchase, DivergenceFacet.Strategy
            })
            {
                foreach (DivergenceKind kind in new[]
                {
                    DivergenceKind.ValueMismatch, DivergenceKind.PhantomInRecon,
                    DivergenceKind.MissingInRecon, DivergenceKind.Consistency
                })
                {
                    var d = new LedgerDivergence
                    {
                        Facet = facet,
                        Kind = kind,
                        Identity = "x",
                        Detail = "guidCorroborated=true" // even this must not promote
                    };
                    Assert.False(LedgerDivergenceReport.IsAlwaysHard(d),
                        $"facet={facet} kind={kind} must be report-only");
                }
            }
        }

        [Fact]
        public void Format_StableAndComplete()
        {
            // Guards: a divergence dropped from the formatted report.
            var save = HealthySave();
            save.SubjectScience["s@Kerbin"] = 5.0;
            var recon = HealthyRecon();
            recon.Funds = 40000.0;                 // funds divergence
            recon.SubjectScience["s@Kerbin"] = 9.0; // subject divergence

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            string formatted = report.Format();

            // Header reflects the actual count.
            Assert.Contains($"total={report.All.Count}", formatted);
            // Every divergence appears as its own line.
            foreach (var d in report.All)
                Assert.Contains(d.ToString(), formatted);
            // Both facets present.
            Assert.Contains("facet=Funds", formatted);
            Assert.Contains("facet=SubjectScience", formatted);
            // One line per divergence + the header line.
            int lineCount = formatted.Split('\n').Length;
            Assert.Equal(report.All.Count + 1, lineCount);
        }
    }
}
