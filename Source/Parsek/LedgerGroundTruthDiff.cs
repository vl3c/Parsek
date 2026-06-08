using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure, headless-testable diff between a parsed ground-truth save
    /// (<see cref="CareerSaveSnapshot"/>) and a ledger reconstruction
    /// (<see cref="LedgerReconstructionSnapshot"/>). Emits a
    /// <see cref="LedgerDivergenceReport"/> tagging each disagreement by facet
    /// and kind. No Unity scene access; the facility maxLevel map is injected so
    /// the fraction&lt;-&gt;int conversion stays Unity-free and testable.
    ///
    /// Facet policy (see design Behavior):
    ///   - Funds / SciencePool / Reputation: HARD (seeded pools; within tolerance).
    ///   - Per-identity facets (subject science, facilities, contracts, milestones)
    ///     and phantoms: REPORT-ONLY by default, promoted to hard only when
    ///     StrictPerIdentityForTesting is true.
    ///   - Vessel recovery consistency: HARD when guid-corroborated, else report-only.
    ///   - A facet is skipped entirely when the save lacks it (save.HasX false).
    ///
    /// See docs/dev/design-ledger-groundtruth-harness.md.
    /// </summary>
    internal static class LedgerGroundTruthDiff
    {
        private const string Tag = "LedgerGroundTruth";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Promotes ALL report-only per-identity / phantom / uncorroborated
        /// divergences to hard failures. Default false: only the seeded pools
        /// and guid-corroborated recovery consistency fail on a real
        /// mixed-history career. Set true only on a clean test career flown
        /// entirely under Parsek tracking.
        /// </summary>
        internal static bool StrictPerIdentityForTesting = false;

        /// <summary>
        /// Compares the reconstruction against the parsed save. Pure. The
        /// <paramref name="facilityMaxLevels"/> map (facilityId -&gt; 0-based max
        /// index, e.g. 2 for a 3-tier facility) is injected from live KSP so the
        /// facility fraction&lt;-&gt;int conversion stays testable.
        /// </summary>
        internal static LedgerDivergenceReport Compare(
            CareerSaveSnapshot save,
            LedgerReconstructionSnapshot recon,
            FacetTolerances tol,
            IReadOnlyDictionary<string, int> facilityMaxLevels)
        {
            var report = new LedgerDivergenceReport();

            if (save == null || recon == null)
            {
                ParsekLog.Verbose(Tag,
                    $"Compare: null input (save={(save == null ? "null" : "ok")}, " +
                    $"recon={(recon == null ? "null" : "ok")}) -> empty report");
                return report;
            }

            CompareFunds(save, recon, tol, report);
            CompareSciencePool(save, recon, tol, report);
            CompareReputation(save, recon, tol, report);
            CompareSubjectScience(save, recon, tol, report);
            CompareFacilities(save, recon, facilityMaxLevels, report);
            CompareContracts(save, recon, report);
            CompareMilestones(save, recon, report);
            CompareRecovery(save, recon, report);

            int hard = report.HardFailures(StrictPerIdentityForTesting).Count;
            int reportOnly = report.All.Count - hard;
            ParsekLog.Verbose(Tag,
                $"Compare: result divergences={report.All.Count.ToString(IC)} " +
                $"hardFailures={hard.ToString(IC)} reportOnly={reportOnly.ToString(IC)} " +
                $"facetsCompared={report.FacetsCompared.ToString(IC)} " +
                $"strict={StrictPerIdentityForTesting.ToString(IC)}");

            return report;
        }

        // ----------------------------------------------------------------
        // Scalar pools (HARD, within tolerance)
        // ----------------------------------------------------------------

        private static void CompareFunds(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report)
        {
            if (!save.HasFunds)
            {
                ParsekLog.Verbose(Tag, "CompareFunds: save has no funds facet -> skip");
                return;
            }
            report.FacetsCompared++;

            double delta = Math.Abs(save.Funds - recon.Funds);
            bool within = delta <= tol.Funds;
            ParsekLog.Verbose(Tag,
                $"funds save={save.Funds.ToString("R", IC)} recon={recon.Funds.ToString("R", IC)} " +
                $"delta={delta.ToString("R", IC)} within tol={within.ToString(IC)}");

            if (!within)
            {
                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.Funds,
                    Kind = DivergenceKind.ValueMismatch,
                    Identity = "",
                    ExpectedFromSave = save.Funds,
                    Reconstructed = recon.Funds,
                    Detail = $"funds delta={delta.ToString("R", IC)} tol={tol.Funds.ToString("R", IC)}"
                });
            }
        }

        private static void CompareSciencePool(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report)
        {
            if (!save.HasScience)
            {
                ParsekLog.Verbose(Tag, "CompareSciencePool: save has no science facet -> skip");
                return;
            }
            report.FacetsCompared++;

            double delta = Math.Abs(save.SciencePool - recon.SciencePool);
            bool within = delta <= tol.SciencePool;
            ParsekLog.Verbose(Tag,
                $"sciencePool save={save.SciencePool.ToString("R", IC)} recon={recon.SciencePool.ToString("R", IC)} " +
                $"delta={delta.ToString("R", IC)} within tol={within.ToString(IC)}");

            if (!within)
            {
                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.SciencePool,
                    Kind = DivergenceKind.ValueMismatch,
                    Identity = "",
                    ExpectedFromSave = save.SciencePool,
                    Reconstructed = recon.SciencePool,
                    Detail = $"sciencePool delta={delta.ToString("R", IC)} tol={tol.SciencePool.ToString("R", IC)}"
                });
            }
        }

        private static void CompareReputation(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report)
        {
            if (!save.HasRep)
            {
                ParsekLog.Verbose(Tag, "CompareReputation: save has no rep facet -> skip");
                return;
            }
            report.FacetsCompared++;

            double delta = Math.Abs(save.Reputation - recon.Reputation);
            bool within = delta <= tol.Reputation;
            ParsekLog.Verbose(Tag,
                $"reputation save={save.Reputation.ToString("R", IC)} recon={recon.Reputation.ToString("R", IC)} " +
                $"delta={delta.ToString("R", IC)} within tol={within.ToString(IC)}");

            if (!within)
            {
                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.Reputation,
                    Kind = DivergenceKind.ValueMismatch,
                    Identity = "",
                    ExpectedFromSave = save.Reputation,
                    Reconstructed = recon.Reputation,
                    Detail = $"reputation delta={delta.ToString("R", IC)} tol={tol.Reputation.ToString("R", IC)}"
                });
            }
        }

        // ----------------------------------------------------------------
        // Per-subject science (REPORT-ONLY by default)
        // ----------------------------------------------------------------

        private static void CompareSubjectScience(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report)
        {
            // Per-identity facet: only diff when the save actually has the
            // ResearchAndDevelopment facet (HasScience also gates the subject
            // dict, since both come from the same SCENARIO).
            if (!save.HasScience)
            {
                ParsekLog.Verbose(Tag, "CompareSubjectScience: save has no R&D facet -> skip");
                return;
            }
            report.FacetsCompared++;

            int mismatches = 0;
            int phantoms = 0;

            // Recon subject vs save subject.
            foreach (var kvp in recon.SubjectScience)
            {
                string id = kvp.Key;
                double reconSci = kvp.Value;
                if (save.SubjectScience.TryGetValue(id, out double saveSci))
                {
                    if (Math.Abs(saveSci - reconSci) > tol.Subject)
                    {
                        report.All.Add(new LedgerDivergence
                        {
                            Facet = DivergenceFacet.SubjectScience,
                            Kind = DivergenceKind.ValueMismatch,
                            Identity = id,
                            ExpectedFromSave = saveSci,
                            Reconstructed = reconSci,
                            Detail = $"subject science mismatch tol={tol.Subject.ToString("R", IC)}"
                        });
                        mismatches++;
                    }
                }
                else
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.SubjectScience,
                        Kind = DivergenceKind.PhantomInRecon,
                        Identity = id,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = reconSci,
                        Detail = "subject credited in recon but absent from save"
                    });
                    phantoms++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"CompareSubjectScience: reconSubjects={recon.SubjectScience.Count.ToString(IC)} " +
                $"saveSubjects={save.SubjectScience.Count.ToString(IC)} " +
                $"mismatches={mismatches.ToString(IC)} phantoms={phantoms.ToString(IC)}");
        }

        // ----------------------------------------------------------------
        // Facilities (REPORT-ONLY by default); compare in 0-based int space
        // ----------------------------------------------------------------

        private static void CompareFacilities(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            IReadOnlyDictionary<string, int> facilityMaxLevels,
            LedgerDivergenceReport report)
        {
            if (save.FacilityLevelFrac.Count == 0)
            {
                ParsekLog.Verbose(Tag, "CompareFacilities: save has no facility facet -> skip");
                return;
            }
            report.FacetsCompared++;

            int mismatches = 0;
            int phantoms = 0;

            foreach (var kvp in recon.FacilityLevel)
            {
                string facilityId = kvp.Key;
                int reconLevel1 = kvp.Value;
                int reconLevel0 = FacilityStatePatcher.ToKspFacilityLevel(reconLevel1);

                if (save.FacilityLevelFrac.TryGetValue(facilityId, out double saveFrac))
                {
                    int maxLevel0 = 0;
                    if (facilityMaxLevels != null)
                        facilityMaxLevels.TryGetValue(facilityId, out maxLevel0);

                    int saveLevel0 = (int)Math.Round(saveFrac * maxLevel0, MidpointRounding.AwayFromZero);

                    if (saveLevel0 != reconLevel0)
                    {
                        report.All.Add(new LedgerDivergence
                        {
                            Facet = DivergenceFacet.Facility,
                            Kind = DivergenceKind.ValueMismatch,
                            Identity = facilityId,
                            ExpectedFromSave = saveLevel0,
                            Reconstructed = reconLevel0,
                            Detail = $"facility level mismatch saveFrac={saveFrac.ToString("R", IC)} " +
                                     $"maxLevel0={maxLevel0.ToString(IC)} reconLevel1={reconLevel1.ToString(IC)}"
                        });
                        mismatches++;
                    }
                }
                else
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Facility,
                        Kind = DivergenceKind.PhantomInRecon,
                        Identity = facilityId,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = reconLevel0,
                        Detail = "facility tracked in recon but absent from save"
                    });
                    phantoms++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"CompareFacilities: reconFacilities={recon.FacilityLevel.Count.ToString(IC)} " +
                $"saveFacilities={save.FacilityLevelFrac.Count.ToString(IC)} " +
                $"mismatches={mismatches.ToString(IC)} phantoms={phantoms.ToString(IC)}");
        }

        // ----------------------------------------------------------------
        // Contracts (REPORT-ONLY by default)
        // ----------------------------------------------------------------

        private static void CompareContracts(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (save.ContractGuidsAllStates.Count == 0 && save.ActiveContractGuids.Count == 0)
            {
                ParsekLog.Verbose(Tag, "CompareContracts: save has no contract facet -> skip");
                return;
            }
            report.FacetsCompared++;

            int phantoms = 0;
            int missing = 0;

            // Recon-active guids absent from save's all-states set => phantom.
            // (Absent from active but present in all-states is a benign
            // state-transition the recon may legitimately not have captured;
            // surface it as a ValueMismatch report entry.)
            foreach (string guid in recon.ActiveContractGuids)
            {
                if (!save.ContractGuidsAllStates.Contains(guid))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Contract,
                        Kind = DivergenceKind.PhantomInRecon,
                        Identity = guid,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "contract active in recon but absent from save"
                    });
                    phantoms++;
                }
                else if (!save.ActiveContractGuids.Contains(guid))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Contract,
                        Kind = DivergenceKind.ValueMismatch,
                        Identity = guid,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "contract active in recon but not Active in save"
                    });
                    phantoms++;
                }
            }

            // Save-active guids the recon does not consider active => missing.
            foreach (string guid in save.ActiveContractGuids)
            {
                if (!recon.ActiveContractGuids.Contains(guid))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Contract,
                        Kind = DivergenceKind.MissingInRecon,
                        Identity = guid,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "contract Active in save but not active in recon"
                    });
                    missing++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"CompareContracts: reconActive={recon.ActiveContractGuids.Count.ToString(IC)} " +
                $"saveActive={save.ActiveContractGuids.Count.ToString(IC)} " +
                $"phantoms={phantoms.ToString(IC)} missing={missing.ToString(IC)}");
        }

        // ----------------------------------------------------------------
        // Milestones (REPORT-ONLY by default)
        // ----------------------------------------------------------------

        private static void CompareMilestones(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (save.AllMilestoneIds.Count == 0 && save.CompletedMilestoneIds.Count == 0)
            {
                ParsekLog.Verbose(Tag, "CompareMilestones: save has no milestone facet -> skip");
                return;
            }
            report.FacetsCompared++;

            int phantoms = 0;
            int missing = 0;

            // Recon-credited ids that match NEITHER the qualified NOR bare save
            // id form => phantom. (The parser already emitted both forms, so a
            // single Contains check against AllMilestoneIds covers both.)
            foreach (string id in recon.CreditedMilestoneIds)
            {
                if (!save.AllMilestoneIds.Contains(id))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Milestone,
                        Kind = DivergenceKind.PhantomInRecon,
                        Identity = id,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "milestone credited in recon but absent from save"
                    });
                    phantoms++;
                }
            }

            // Save-completed ids the recon did not credit => missing.
            foreach (string id in save.CompletedMilestoneIds)
            {
                if (!recon.CreditedMilestoneIds.Contains(id))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Milestone,
                        Kind = DivergenceKind.MissingInRecon,
                        Identity = id,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "milestone completed in save but not credited in recon"
                    });
                    missing++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"CompareMilestones: reconCredited={recon.CreditedMilestoneIds.Count.ToString(IC)} " +
                $"saveCompleted={save.CompletedMilestoneIds.Count.ToString(IC)} " +
                $"phantoms={phantoms.ToString(IC)} missing={missing.ToString(IC)}");
        }

        // ----------------------------------------------------------------
        // Vessel recovery consistency (HARD when guid-corroborated)
        // ----------------------------------------------------------------

        private static void CompareRecovery(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (recon.RecoveryCredits == null || recon.RecoveryCredits.Count == 0)
            {
                ParsekLog.Verbose(Tag, "CompareRecovery: no recovery credits -> skip");
                return;
            }
            report.FacetsCompared++;

            int hardViolations = 0;
            int reportOnly = 0;
            int consistent = 0;

            foreach (var credit in recon.RecoveryCredits)
            {
                // A recovered vessel must be ABSENT from save.Vessels. Correlate
                // by guid (preferred) or pid (craft-baked caveat: pid-only is not
                // proof of identity).
                bool guidMatch = false;
                bool pidMatch = false;

                if (!string.IsNullOrEmpty(credit.VesselGuid))
                {
                    foreach (var v in save.Vessels)
                    {
                        if (!string.IsNullOrEmpty(v.Pid)
                            && string.Equals(v.Pid, credit.VesselGuid, StringComparison.OrdinalIgnoreCase))
                        {
                            guidMatch = true;
                            break;
                        }
                    }
                }

                if (!guidMatch && credit.VesselPid != 0)
                {
                    foreach (var v in save.Vessels)
                    {
                        if (v.PersistentId == credit.VesselPid)
                        {
                            pidMatch = true;
                            break;
                        }
                    }
                }

                if (guidMatch)
                {
                    // Recovered vessel still present, identity corroborated by
                    // guid => HARD consistency divergence.
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Vessel,
                        Kind = DivergenceKind.Consistency,
                        Identity = credit.VesselGuid,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = credit.Amount,
                        Detail = $"recovery credit for recordingId={credit.RecordingId ?? "(none)"} " +
                                 $"vessel='{credit.VesselName ?? "(none)"}' but vessel still present in save " +
                                 $"guidCorroborated=true"
                    });
                    hardViolations++;
                }
                else if (pidMatch)
                {
                    // pid-only match: not proof of identity (craft-baked-pid
                    // caveat) => report-only consistency entry.
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Vessel,
                        Kind = DivergenceKind.Consistency,
                        Identity = credit.VesselPid.ToString(IC),
                        ExpectedFromSave = double.NaN,
                        Reconstructed = credit.Amount,
                        Detail = $"recovery credit for recordingId={credit.RecordingId ?? "(none)"} " +
                                 $"vessel='{credit.VesselName ?? "(none)"}' matched a present vessel by pid only " +
                                 $"guidCorroborated=false"
                    });
                    reportOnly++;
                }
                else
                {
                    consistent++;
                }
            }

            ParsekLog.Verbose(Tag,
                $"CompareRecovery: credits={recon.RecoveryCredits.Count.ToString(IC)} " +
                $"hardViolations={hardViolations.ToString(IC)} " +
                $"reportOnly={reportOnly.ToString(IC)} consistent={consistent.ToString(IC)}");
        }
    }
}
