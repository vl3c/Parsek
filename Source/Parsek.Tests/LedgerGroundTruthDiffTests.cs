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
