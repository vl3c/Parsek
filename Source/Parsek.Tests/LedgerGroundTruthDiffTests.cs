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

        // ================================================================
        // career-ledger B.4: the RunTests `strict` arg is the per-scenario seam
        // ================================================================

        [Theory]
        [InlineData(null, false)]
        [InlineData("false", false)]
        [InlineData("true", true)]
        public void StrictSeam_RunTestsArgDrivesTheStaticAndThenTheHardSet(
            string strictRaw, bool expectedHard)
        {
            // THE WHOLE PLUMBING, END TO END, HEADLESSLY: the wire token the harness
            // sends -> TestCommandRunTests.TryParseStrictArg -> the static the addon
            // assigns unconditionally -> the `strict` argument
            // LedgerGroundTruthHarness/Inv8Ledger pass to HardFailures. The middle link
            // is what B.4 added; the two ends already existed and are what make the
            // addition observable without Unity.
            //
            // The ABSENT row is the one that matters most: it is every committed spec,
            // and it must land on the same hard set as an explicit "false".
            bool strict;
            Assert.True(Parsek.TestCommands.TestCommandRunTests.TryParseStrictArg(
                strictRaw, out strict));
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = strict;

            var save = HealthySave();
            var recon = HealthyRecon();
            recon.SubjectScience["phantom@Duna"] = 3.0; // report-only by default

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            // The divergence exists on every row; only its SEVERITY moves.
            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.SubjectScience
                && d.Kind == DivergenceKind.PhantomInRecon);

            var hard = report.HardFailures(
                LedgerGroundTruthDiff.StrictPerIdentityForTesting);
            Assert.Equal(expectedHard,
                hard.Exists(d => d.Facet == DivergenceFacet.SubjectScience));
        }

        [Fact]
        public void StrictSeam_UnarmedBatchStillGreensOnAReportOnlyDivergence()
        {
            // WHAT AN UNARMED RUN ACTUALLY ASSERTS, stated as behaviour rather than as a
            // field value: with no `strict` arg the in-game cell's
            // InGameAssert.IsTrue(hard == 0) must still pass over a report carrying
            // report-only divergences. No committed harness spec arms `strict`
            // (career-ledger B.4 deferred arming for want of a subject with populated
            // per-identity facets), so this is the state every unattended run of the
            // ground-truth category is in today - and a default flip would red here
            // rather than on a nightly.
            bool strict;
            Assert.True(Parsek.TestCommands.TestCommandRunTests.TryParseStrictArg(
                null, out strict));
            LedgerGroundTruthDiff.StrictPerIdentityForTesting = strict;

            var save = HealthySave();
            var recon = HealthyRecon();
            recon.SubjectScience["phantom@Duna"] = 3.0;

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.NotEmpty(report.All);
            Assert.Empty(report.HardFailures(
                LedgerGroundTruthDiff.StrictPerIdentityForTesting));
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
        public void Diff_MilestoneMissing_IsFormAgnosticLikeThePhantomDirection()
        {
            // THE ASYMMETRY THE 2026-08-20 PARSER FIX WOULD OTHERWISE HAVE EXPOSED.
            // The parser emits BOTH spellings of every milestone, so a nested
            // milestone the recon credits QUALIFIED used to be reported "missing" for
            // its BARE twin - a divergence manufactured by the save-side parser's own
            // double emission, and one strict promotes to a hard failure. The phantom
            // direction was already form-agnostic; this pins that the reverse is too.
            //
            // Latent until now only because no committed career fixture carried a
            // COMPLETED NESTED milestone. `career-earned-pad` does (`Kerbin/Science`).
            var save = HealthySave();
            save.AllMilestoneIds.Add("Kerbin/Science");
            save.AllMilestoneIds.Add("Science");
            save.CompletedMilestoneIds.Add("Kerbin/Science");
            save.CompletedMilestoneIds.Add("Science");   // the parser's bare twin
            var recon = HealthyRecon();
            recon.CreditedMilestoneIds.Add("Kerbin/Science");  // qualified form ONLY

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            // No Milestone divergence at all - which subsumes "no MissingInRecon"
            // and additionally pins that the qualified form did not go phantom.
            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Milestone);
        }

        [Fact]
        public void Diff_MilestoneMissing_TopLevelIdIsNotSilencedByANestedNamesake()
        {
            // THE BOUNDARY OF THE SUPPRESSION, pinned so it cannot widen. The rule is
            // "a bare twin the PARSER minted from a qualified id it ALSO emitted is not
            // an independent claim" - keyed on the save's own double emission, NOT on a
            // last-segment match against the recon. Keying it on the recon would also
            // silence a genuinely completed TOP-LEVEL milestone whenever the recon
            // credited some `Body/<same name>`, which is a real (if stock-unreachable)
            // hole rather than the double-emission artifact being cancelled.
            //
            // Here the save completed a TOP-LEVEL `Landing` - no `X/Landing` among its
            // completed ids, so it has no qualified parent and is nobody's twin - while
            // the recon credits only `Mun/Landing`. That is a real disagreement and
            // must still fire.
            var save = HealthySave();
            save.AllMilestoneIds.Add("Landing");
            save.CompletedMilestoneIds.Add("Landing");
            var recon = HealthyRecon();
            recon.CreditedMilestoneIds.Add("Mun/Landing");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "Landing");
        }

        [Fact]
        public void Diff_MilestoneMissing_StillFiresWhenNoFormIsCredited()
        {
            // The other side of the form-agnostic check: relaxing the spelling must
            // not relax the FACT. A save-completed milestone the recon credits under
            // NO spelling is still missing.
            var save = HealthySave();
            save.AllMilestoneIds.Add("Kerbin/Science");
            save.CompletedMilestoneIds.Add("Kerbin/Science");
            var recon = HealthyRecon();
            recon.CreditedMilestoneIds.Add("Mun/Landing");   // a different milestone

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Milestone
                && d.Kind == DivergenceKind.MissingInRecon
                && d.Identity == "Kerbin/Science");
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
        public void Diff_PermanentlyHeldKerbalListedAvailable_IsNotADivergence()
        {
            // Guards a FALSE POSITIVE. A permanently-reserved kerbal listed
            // "Available" is an explained production state, not drift:
            // KerbalsModule.ApplyToRoster never writes rosterStatus, so a death still in
            // the save clock's future (a rewind before it) or an unstamped pre-S8 death
            // that stock respawned leaves the kerbal Available and held. This cell
            // previously asserted the divergence - it was pinning the false positive.
            var save = SaveWithRoster(Kerbal("Bill Kerman", "Available"));
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;
            recon.PermanentlyGoneKerbals.Add("Bill Kerman");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.DoesNotContain(report.All, d => d.Facet == DivergenceFacet.Roster);
            // The signal stays OBSERVABLE as a census count, just not as a divergence.
            Assert.Contains(logLines, l => l.Contains("CompareRoster")
                && l.Contains("availableButPermanentlyHeld=1"));
        }

        [Fact]
        public void Diff_PermanentlyGoneKerbalInAnUnexplainableState_ConsistencyReportOnly()
        {
            // The carve-out is exactly Available: a state the reservation cannot
            // explain (Assigned - flying a mission) is still a Consistency entry.
            // Report-only even though it is a Consistency kind: only a
            // GUID-CORROBORATED Vessel consistency entry is always-hard, and a roster
            // name is not a launch guid.
            var save = SaveWithRoster(Kerbal("Bill Kerman", "Assigned"));
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;
            recon.PermanentlyGoneKerbals.Add("Bill Kerman");

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Roster
                && d.Kind == DivergenceKind.Consistency
                && d.Identity == "Bill Kerman"
                && d.Detail.Contains("state='Assigned'"));
            Assert.Empty(report.HardFailures(strict: false));
        }

        [Fact]
        public void Diff_EmptyButExportedRoster_StillComparesAndRaisesPhantoms()
        {
            // Guards the facet gate: it is the HasRoster FLAG ALONE. An EMPTY parsed
            // roster is a real state (a wiped facet), and a recon claiming it created
            // a kerbal must raise a phantom against it. An "|| Count == 0" gate
            // silently greened exactly this case.
            var save = HealthySave();
            save.HasRoster = true;              // exported, and EMPTY
            var recon = HealthyRecon();
            recon.HasRosterSurface = true;
            recon.LedgerCreatedKerbals.Add("Phantomly Kerman");

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.Roster
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "Phantomly Kerman");
            Assert.Equal(baseline.FacetsCompared + 1, report.FacetsCompared);
        }

        [Fact]
        public void Diff_EmptyButExportedTechTree_StillComparesAndRaisesPhantoms()
        {
            // Same gate on the tech facet: HasTechTree alone decides.
            var save = HealthySave();
            save.HasTechTree = true;            // exported, and EMPTY
            var recon = HealthyRecon();
            recon.HasTechSurface = true;
            recon.ResearchedTechIds.Add("heavyRocketry");

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Contains(report.All, d =>
                d.Facet == DivergenceFacet.TechNode
                && d.Kind == DivergenceKind.PhantomInRecon
                && d.Identity == "heavyRocketry");
            Assert.Equal(baseline.FacetsCompared + 1, report.FacetsCompared);
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
        public void Diff_PartPurchases_SaveSideCensusOnly()
        {
            // The facet is CENSUS ONLY - nothing on the reconstruction side produces a
            // purchased-part set, so there is no compare half to exercise. What is
            // guarded is that the census still emits and no divergence is invented.
            var save = HealthySave();
            save.HasTechTree = true;
            save.UnlockedTechIds.Add("start");
            save.PurchasedPartNames.Add("mk1pod.v2");
            save.TechNodePartCounts["start"] = 1;
            var recon = HealthyRecon();

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Equal(baseline.FacetsCompared, report.FacetsCompared);
            Assert.Contains(logLines, l => l.Contains("ComparePartPurchases")
                && l.Contains("no reconstruction part-purchase surface exists")
                && l.Contains("saveParts=1"));
        }

        private static CareerSaveSnapshot SaveWithActiveStrategies(params string[] ids)
        {
            var save = HealthySave();
            save.HasStrategySystem = true;
            foreach (string id in ids)
            {
                save.Strategies.Add(new SaveStrategy { Name = id, IsActive = true, ActivatedUT = 100.0, Factor = 0.05 });
                save.ActiveStrategyIds.Add(id);
            }
            return save;
        }

        private static LedgerReconstructionSnapshot ReconWithStrategies(string[] managed, string[] active)
        {
            var recon = HealthyRecon();
            recon.HasStrategySurface = true;
            foreach (string id in managed) recon.ManagedStrategyIds.Add(id);
            foreach (string id in active) recon.LedgerActiveStrategyIds.Add(id);
            return recon;
        }

        [Fact]
        public void Diff_Strategies_NoManagedStrategy_StaysUncompared()
        {
            // A career whose ledger never activated a strategy keeps its facet count, so the
            // committed specs pinning facetsCompared= are unchanged; the save's active set is
            // a census line only.
            var save = SaveWithActiveStrategies("PatentsLicensingCfg");
            var recon = ReconWithStrategies(new string[0], new string[0]);

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Equal(baseline.FacetsCompared, report.FacetsCompared);
            Assert.Empty(report.All);
            Assert.Contains(logLines, l => l.Contains("CompareStrategies: no ledger-managed strategy")
                && l.Contains("saveActive=1"));
        }

        [Fact]
        public void Diff_Strategies_MatchingSets_CountFacetNoDivergence()
        {
            // Appreciation on in both; the one-shot exchanger the ledger saw end is off in both.
            var save = SaveWithActiveStrategies("AppreciationCampaignCfg");
            var recon = ReconWithStrategies(
                new[] { "AppreciationCampaignCfg", "BailoutGrant" },
                new[] { "AppreciationCampaignCfg" });

            var baseline = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Equal(baseline.FacetsCompared + 1, report.FacetsCompared);
            Assert.Empty(report.All);
            Assert.Contains(logLines, l => l.Contains("[LedgerGroundTruth]")
                && l.Contains("CompareStrategies: managed=[AppreciationCampaignCfg,BailoutGrant] ledgerActive=[AppreciationCampaignCfg] saveActive=[AppreciationCampaignCfg] diverged=0 [] unmanagedSaveActive=0"));
        }

        [Fact]
        public void Diff_Strategies_LedgerActiveNotInSave_IsPhantomReportOnly()
        {
            // A committed activation the clock reached that stock never switched on.
            var save = SaveWithActiveStrategies();
            var recon = ReconWithStrategies(
                new[] { "AppreciationCampaignCfg" }, new[] { "AppreciationCampaignCfg" });

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            var d = Assert.Single(report.All);
            Assert.Equal(DivergenceFacet.Strategy, d.Facet);
            Assert.Equal(DivergenceKind.PhantomInRecon, d.Kind);
            Assert.Equal("AppreciationCampaignCfg", d.Identity);
            Assert.Empty(report.HardFailures(strict: false));
            Assert.Single(report.HardFailures(strict: true));
            Assert.Contains(logLines, l => l.Contains("CompareStrategies:")
                && l.Contains("diverged=1 [AppreciationCampaignCfg]"));
        }

        [Fact]
        public void Diff_Strategies_SaveActiveNotInLedger_IsMissingInRecon()
        {
            // An activation switched on before its committed UT (or a deactivation stock
            // never applied): managed, on in the save, off in the ledger at the clock.
            var save = SaveWithActiveStrategies("AppreciationCampaignCfg");
            var recon = ReconWithStrategies(new[] { "AppreciationCampaignCfg" }, new string[0]);

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            var d = Assert.Single(report.All);
            Assert.Equal(DivergenceFacet.Strategy, d.Facet);
            Assert.Equal(DivergenceKind.MissingInRecon, d.Kind);
            Assert.Equal(1.0, d.ExpectedFromSave);
            Assert.Equal(0.0, d.Reconstructed);
        }

        [Fact]
        public void Diff_Strategies_UnmanagedSaveActive_IsCountedNotEmitted()
        {
            // Leadership Initiative on since before the save had a ledger: not the ledger's
            // to state, so no divergence even though the ledger has it off.
            var save = SaveWithActiveStrategies("LeadershipInitiative", "AppreciationCampaignCfg");
            var recon = ReconWithStrategies(
                new[] { "AppreciationCampaignCfg" }, new[] { "AppreciationCampaignCfg" });

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            Assert.Empty(report.All);
            Assert.Contains(logLines, l => l.Contains("CompareStrategies:")
                && l.Contains("diverged=0 []") && l.Contains("unmanagedSaveActive=1"));
        }

        [Fact]
        public void Diff_NewFacets_NeverPromoteToAlwaysHard()
        {
            // Guards: a new facet accidentally joining the always-hard set. Only the
            // seeded pools and guid-corroborated vessel consistency may be hard
            // regardless of strictness.
            foreach (DivergenceFacet facet in new[]
            {
                DivergenceFacet.Roster, DivergenceFacet.TechNode, DivergenceFacet.Strategy
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

        // ================================================================
        // StockPoolAdjustment rows are left out of the pool comparison
        // ================================================================

        [Fact]
        public void Diff_ScienceSeedDefectHiddenByAnAdjustmentRow_StillHardFails()
        {
            // LAB-12's DEFECT 1 shape: the walk seeded science 0 where the save had 400, the
            // stock pool reference filed the 400 gap as an adjustment row, and the walk
            // carries it, so the running value matches the save. Left out, the ledger's own
            // rows reconstruct -251.375 against the save's 148.625: a hard divergence.
            var save = HealthySave();
            save.SciencePool = 148.625;
            var recon = HealthyRecon();
            recon.SciencePool = 148.625;
            recon.ScienceAdjustment = 400.0;
            recon.ScienceAdjustmentRows = 1;

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            var hard = report.HardFailures(strict: false);
            var science = Assert.Single(hard, d => d.Facet == DivergenceFacet.SciencePool);
            Assert.Equal(-251.375, science.Reconstructed, 6);
            Assert.Contains("adjustmentsLeftOut=400", science.Detail);
        }

        [Fact]
        public void Diff_ReputationAndFundsAdjustments_AreLeftOutToo()
        {
            var save = HealthySave();
            var recon = HealthyRecon();
            recon.ReputationAdjustment = 25.0;
            recon.ReputationAdjustmentRows = 1;
            recon.FundsAdjustment = -500.0;
            recon.FundsAdjustmentRows = 2;

            var report = LedgerGroundTruthDiff.Compare(
                save, recon, FacetTolerances.Default, NoMaxLevels());

            var hard = report.HardFailures(strict: false);
            var rep = Assert.Single(hard, d => d.Facet == DivergenceFacet.Reputation);
            Assert.Equal(50.0, rep.Reconstructed, 6);
            // Funds left out read ABOVE the save (an unmodelled spend kept): the existing
            // uplift demotion makes it report-only, not hard.
            Assert.DoesNotContain(hard, d => d.Facet == DivergenceFacet.Funds);
            Assert.Contains(report.All, d => d.Facet == DivergenceFacet.Funds
                && d.Kind == DivergenceKind.UpliftClampedExpected && d.Reconstructed == 50500.0);
        }

        [Fact]
        public void Diff_NoAdjustmentRows_ComparesTheRunningValues()
        {
            var report = LedgerGroundTruthDiff.Compare(
                HealthySave(), HealthyRecon(), FacetTolerances.Default, NoMaxLevels());
            Assert.Empty(report.All);
        }

        [Fact]
        public void SumWalkedStockPoolAdjustments_CountsOnlyWalkedRowsOfThePool()
        {
            var walkedScience = StockPoolReference.BuildAdjustmentRow(StockPool.Science, 400.0, 10.0);
            var unwalkedScience = StockPoolReference.BuildAdjustmentRow(StockPool.Science, 7.0, 99.0);
            var funds = StockPoolReference.BuildAdjustmentRow(StockPool.Funds, 3.0, 10.0);
            var earning = new GameAction { ActionId = "e", Type = GameActionType.ScienceEarning, EffectiveScience = 5f };
            var actions = new List<GameAction> { walkedScience, unwalkedScience, funds, earning, null };
            var effects = new Dictionary<string, double>
            {
                [walkedScience.ActionId] = 400.0,
                [funds.ActionId] = 3.0,
                ["e"] = 5.0,
            };

            double total = LedgerGroundTruthDiff.SumWalkedStockPoolAdjustments(
                StockPool.Science, actions, effects, out int rows);
            Assert.Equal(1, rows);
            Assert.Equal(400.0, total, 6);

            Assert.Equal(0.0, LedgerGroundTruthDiff.SumWalkedStockPoolAdjustments(
                StockPool.Science, actions, null, out rows), 6);
            Assert.Equal(0, rows);
        }

        [Fact]
        public void FormatStockPoolAdjustmentLine_NamesPoolRowsTotalAndBothValues()
        {
            string line = LedgerGroundTruthDiff.FormatStockPoolAdjustmentLine(
                "Science", 1, 400.0, 148.625, -251.375);
            Assert.Equal("stock pool adjustments left out of the comparison: pool=Science rows=1 "
                + "total=400 reconWithAdjustments=148.625 reconCompared=-251.375", line);
        }
    }
}
