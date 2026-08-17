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
    ///   - Roster / TechNode: REPORT-ONLY, same promotion rule as the other
    ///     per-identity facets. Each ALSO requires the reconstruction to declare a
    ///     surface (recon.HasXSurface); with no surface the facet logs the save-side
    ///     census and is left UNCOMPARED rather than diffing against an invented
    ///     reconstruction.
    ///   - PartPurchase / Strategy: save-side CENSUS ONLY. Nothing on the
    ///     reconstruction side produces either set, so there is no compare half at
    ///     all (see the method docs for why an unreachable one is worse than none).
    ///   - A facet is skipped entirely when the save lacks it (save.HasX FLAG false).
    ///     The flag is the whole gate: an EMPTY parsed collection is a real state (a
    ///     wiped facet) and must still reach the compare, so recon phantoms fire
    ///     against it.
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
            IReadOnlyDictionary<string, int> facilityMaxLevels,
            bool authoritativeReduction = false)
        {
            var report = new LedgerDivergenceReport();

            if (save == null || recon == null)
            {
                ParsekLog.Verbose(Tag,
                    $"Compare: null input (save={(save == null ? "null" : "ok")}, " +
                    $"recon={(recon == null ? "null" : "ok")}) -> empty report");
                return report;
            }

            CompareFunds(save, recon, tol, report, authoritativeReduction);
            CompareSciencePool(save, recon, tol, report, authoritativeReduction);
            CompareReputation(save, recon, tol, report, authoritativeReduction);
            CompareSubjectScience(save, recon, tol, report);
            CompareFacilities(save, recon, facilityMaxLevels, report);
            CompareContracts(save, recon, report);
            CompareMilestones(save, recon, report);
            CompareRecovery(save, recon, report);
            CompareKerbalCareerLogs(save, recon, report);
            CompareRoster(save, recon, report);
            CompareTechNodes(save, recon, report);
            ComparePartPurchases(save, recon, report);
            CompareStrategies(save, recon, report);

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
        // Kerbal career log (REPORT-ONLY, P9a)
        // ----------------------------------------------------------------

        /// <summary>
        /// Compares the reconstruction's career-log accumulator against the save's roster.
        ///
        /// <para>
        /// ONE-DIRECTIONAL by design: it reports entries the recon credits that the save does
        /// NOT have (<see cref="DivergenceKind.PhantomInRecon"/>), and does not report the
        /// reverse. A kerbal's save-side career log legitimately carries entries the ledger
        /// never saw - pre-Parsek flights, stand-in careers, mod-written entries - so
        /// "the save has more" is the normal state and reporting it would bury the signal.
        /// The production re-assert is monotone in exactly the same direction, so this facet
        /// measures precisely what the patcher would act on.
        /// </para>
        /// </summary>
        private static void CompareKerbalCareerLogs(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (recon.KerbalCareerLog == null || recon.KerbalCareerLog.Count == 0)
            {
                ParsekLog.Verbose(Tag, "CompareKerbalCareerLogs: recon credits no entries -> skip");
                return;
            }
            report.FacetsCompared++;

            int kerbalsCompared = 0;
            int divergent = 0;
            foreach (var kvp in recon.KerbalCareerLog)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                kerbalsCompared++;

                HashSet<KerbalCareerLogEntry> saveEntries = null;
                bool inSave = save.KerbalCareerLog != null
                    && save.KerbalCareerLog.TryGetValue(kvp.Key, out saveEntries);
                if (!inSave) saveEntries = null;

                int missing = 0;
                foreach (var entry in kvp.Value)
                {
                    if (saveEntries == null || !saveEntries.Contains(entry))
                        missing++;
                }

                if (missing == 0) continue;
                divergent++;

                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.KerbalXp,
                    Kind = DivergenceKind.PhantomInRecon,
                    Identity = kvp.Key,
                    ExpectedFromSave = saveEntries != null ? saveEntries.Count : 0,
                    Reconstructed = kvp.Value.Count,
                    Detail = $"kerbalXp kerbal='{kvp.Key}' reconEntries={kvp.Value.Count.ToString(IC)} " +
                             $"saveEntries={(saveEntries != null ? saveEntries.Count : 0).ToString(IC)} " +
                             $"missingInSave={missing.ToString(IC)} inSaveRoster={inSave.ToString(IC)}"
                });
            }

            ParsekLog.Verbose(Tag,
                $"CompareKerbalCareerLogs: kerbals={kerbalsCompared.ToString(IC)} " +
                $"divergent={divergent.ToString(IC)}");
        }

        // ----------------------------------------------------------------
        // Scalar pools (HARD, within tolerance)
        // ----------------------------------------------------------------

        // Classifies a seeded-pool divergence kind. Mirrors the production drawdown guard
        // (KspStatePatcher.ApplyDrawdownGuard / IsGuardableUplift): when no authoritative
        // time-travel context is active and the reconstruction (RAW running balance) runs ABOVE
        // the live save (reconValue beyond tol), the guard would UPLIFT-clamp the patch DOWN to
        // live, so the divergence is the expected missing-channel surplus and is tagged
        // UpliftClampedExpected (report-only). Every other case -- a downward divergence (recon
        // below save), or any divergence while authoritativeReduction is true (guard does not
        // clamp) -- is a hard ValueMismatch. Pure: no Unity / live state.
        internal static DivergenceKind ClassifySeededPoolKind(
            double saveValue, double reconValue, double tol, bool authoritativeReduction)
        {
            if (!authoritativeReduction && reconValue > saveValue + tol)
                return DivergenceKind.UpliftClampedExpected;
            return DivergenceKind.ValueMismatch;
        }

        private static void CompareFunds(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report, bool authoritativeReduction)
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
                DivergenceKind kind = ClassifySeededPoolKind(
                    save.Funds, recon.Funds, tol.Funds, authoritativeReduction);
                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.Funds,
                    Kind = kind,
                    Identity = "",
                    ExpectedFromSave = save.Funds,
                    Reconstructed = recon.Funds,
                    Detail = $"funds delta={delta.ToString("R", IC)} tol={tol.Funds.ToString("R", IC)} " +
                             $"kind={kind} authoritativeReduction={authoritativeReduction.ToString(IC)}"
                });
            }
        }

        private static void CompareSciencePool(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report, bool authoritativeReduction)
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
                DivergenceKind kind = ClassifySeededPoolKind(
                    save.SciencePool, recon.SciencePool, tol.SciencePool, authoritativeReduction);
                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.SciencePool,
                    Kind = kind,
                    Identity = "",
                    ExpectedFromSave = save.SciencePool,
                    Reconstructed = recon.SciencePool,
                    Detail = $"sciencePool delta={delta.ToString("R", IC)} tol={tol.SciencePool.ToString("R", IC)} " +
                             $"kind={kind} authoritativeReduction={authoritativeReduction.ToString(IC)}"
                });
            }
        }

        private static void CompareReputation(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            FacetTolerances tol, LedgerDivergenceReport report, bool authoritativeReduction)
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
                DivergenceKind kind = ClassifySeededPoolKind(
                    save.Reputation, recon.Reputation, tol.Reputation, authoritativeReduction);
                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.Reputation,
                    Kind = kind,
                    Identity = "",
                    ExpectedFromSave = save.Reputation,
                    Reconstructed = recon.Reputation,
                    Detail = $"reputation delta={delta.ToString("R", IC)} tol={tol.Reputation.ToString("R", IC)} " +
                             $"kind={kind} authoritativeReduction={authoritativeReduction.ToString(IC)}"
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
            int mismatches = 0;
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
                    mismatches++;
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
                $"phantoms={phantoms.ToString(IC)} mismatches={mismatches.ToString(IC)} " +
                $"missing={missing.ToString(IC)}");
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

        // ----------------------------------------------------------------
        // Roster (REPORT-ONLY)
        // ----------------------------------------------------------------

        /// <summary>
        /// Compares the ledger's kerbal claims against the save's ROSTER.
        ///
        /// WHAT IS ACTUALLY COMPARED (the reconstruction has no full roster, so
        /// this is deliberately not a roster-vs-roster diff):
        ///   - every kerbal the ledger believes IT created must EXIST in the save
        ///     roster; one that does not is a <see cref="DivergenceKind.PhantomInRecon"/>;
        ///   - every kerbal the ledger holds permanently reserved (dead) must NOT
        ///     be listed in the save roster in a state the reservation cannot
        ///     explain; see the RESPAWN carve-out below.
        /// The opposite direction (save kerbals the ledger never mentions) is NOT a
        /// divergence: the ledger is delta-only and the stock starting four plus
        /// every applicant are legitimately unmentioned.
        ///
        /// THE RESPAWN CARVE-OUT. A permanently-reserved kerbal listed
        /// <c>Available</c> is the DOCUMENTED intended production state, not a
        /// divergence: <see cref="KerbalsModule.ApplyToRoster"/> leaves reserved
        /// kerbals at their natural rosterStatus and performs NO rosterStatus
        /// manipulation when stock's MIA respawn flips a Dead kerbal back to
        /// Available - the reservation persists and CrewDialogFilterPatch keeps them
        /// out of the crew dialog. Flagging that state made the facet raise on
        /// correct behavior. It is counted in the census line instead
        /// (<c>respawnedButReserved</c>), so the signal stays observable without
        /// being a divergence. What DOES still raise is a state the reservation
        /// cannot explain at all (Assigned, Hired, an unrecognized value).
        ///
        /// The gate is the <see cref="CareerSaveSnapshot.HasRoster"/> FLAG ALONE. An
        /// EMPTY parsed roster is a real state (a wiped facet), and must reach the
        /// compare so a recon that claims kerbals raises phantoms against it - the
        /// exact case an <c>|| Count == 0</c> gate silently greened.
        /// With no reconstruction surface the facet logs the save-side census and
        /// is left UNCOMPARED (FacetsCompared is not incremented).
        /// </summary>
        private static void CompareRoster(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (!save.HasRoster)
            {
                ParsekLog.Verbose(Tag, "CompareRoster: save has no roster facet -> skip");
                return;
            }

            // Save-side census, emitted whether or not a reconstruction exists.
            int crew = 0;
            int deadInSave = 0;
            var rosterNames = new HashSet<string>(StringComparer.Ordinal);
            var stateByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in save.Roster)
            {
                if (string.IsNullOrEmpty(k.Name))
                    continue;
                rosterNames.Add(k.Name);
                stateByName[k.Name] = k.State ?? "";
                if (string.Equals(k.Type, "Crew", StringComparison.Ordinal))
                    crew++;
                if (string.Equals(k.State, "Dead", StringComparison.Ordinal))
                    deadInSave++;
            }

            if (!recon.HasRosterSurface)
            {
                ParsekLog.Verbose(Tag,
                    $"CompareRoster: no reconstruction roster surface -> save-side census only " +
                    $"(saveRoster={rosterNames.Count.ToString(IC)} crew={crew.ToString(IC)} " +
                    $"dead={deadInSave.ToString(IC)}) uncompared");
                return;
            }
            report.FacetsCompared++;

            int phantoms = 0;
            foreach (string name in recon.LedgerCreatedKerbals)
            {
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!rosterNames.Contains(name))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.Roster,
                        Kind = DivergenceKind.PhantomInRecon,
                        Identity = name,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "kerbal created in recon but absent from the save roster"
                    });
                    phantoms++;
                }
            }

            int aliveButGone = 0;
            int respawnedButReserved = 0;
            foreach (string name in recon.PermanentlyGoneKerbals)
            {
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!stateByName.TryGetValue(name, out string state))
                    continue; // absent from the roster is exactly what "gone" means.
                if (string.Equals(state, "Dead", StringComparison.Ordinal)
                    || string.Equals(state, "Missing", StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(state, "Available", StringComparison.Ordinal))
                {
                    // DOCUMENTED intended production state (KerbalsModule.ApplyToRoster,
                    // "MIA Respawn"): stock respawns a Dead kerbal to Available and Parsek
                    // deliberately does NOT touch rosterStatus - the reservation persists
                    // and the crew-dialog filter keeps them hidden. Counted, not flagged.
                    respawnedButReserved++;
                    continue;
                }

                report.All.Add(new LedgerDivergence
                {
                    Facet = DivergenceFacet.Roster,
                    Kind = DivergenceKind.Consistency,
                    Identity = name,
                    ExpectedFromSave = double.NaN,
                    Reconstructed = double.NaN,
                    Detail = $"recon holds kerbal permanently gone but the save roster lists " +
                             $"state='{state}' guidCorroborated=false"
                });
                aliveButGone++;
            }

            ParsekLog.Verbose(Tag,
                $"CompareRoster: saveRoster={rosterNames.Count.ToString(IC)} crew={crew.ToString(IC)} " +
                $"deadInSave={deadInSave.ToString(IC)} " +
                $"reconCreated={recon.LedgerCreatedKerbals.Count.ToString(IC)} " +
                $"reconPermanentlyGone={recon.PermanentlyGoneKerbals.Count.ToString(IC)} " +
                $"phantoms={phantoms.ToString(IC)} aliveButGone={aliveButGone.ToString(IC)} " +
                $"respawnedButReserved={respawnedButReserved.ToString(IC)} (stock MIA respawn, expected)");
        }

        // ----------------------------------------------------------------
        // Tech nodes (REPORT-ONLY)
        // ----------------------------------------------------------------

        /// <summary>
        /// Compares the tech nodes the ledger claims were researched against the
        /// save's unlocked set.
        ///
        /// ONE DIRECTION ONLY, deliberately: the reconstruction's set comes from the
        /// ledger's ScienceSpending rows, which are a DELTA (nodes researched while
        /// Parsek was installed), while the save's set is ABSOLUTE (every node ever
        /// unlocked). "Unlocked in the save but not claimed by recon" is therefore
        /// the NORMAL state of every mixed-history career and would emit one noise
        /// entry per pre-Parsek node; it is counted in the summary log instead. The
        /// meaningful claim is the other way round: a node the ledger says it
        /// researched that the save does NOT list unlocked
        /// (<see cref="DivergenceKind.PhantomInRecon"/>).
        ///
        /// The gate is the <see cref="CareerSaveSnapshot.HasTechTree"/> FLAG ALONE.
        /// An EMPTY parsed unlock set is a real state (a wiped tech tree), and must
        /// reach the compare so a recon claiming researched nodes raises phantoms
        /// against it - the exact case an <c>|| Count == 0</c> gate silently greened.
        /// </summary>
        private static void CompareTechNodes(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (!save.HasTechTree)
            {
                ParsekLog.Verbose(Tag, "CompareTechNodes: save has no tech facet -> skip");
                return;
            }

            if (!recon.HasTechSurface)
            {
                ParsekLog.Verbose(Tag,
                    $"CompareTechNodes: no reconstruction tech surface -> save-side count only " +
                    $"(saveUnlocked={save.UnlockedTechIds.Count.ToString(IC)}) uncompared");
                return;
            }
            report.FacetsCompared++;

            int phantoms = 0;
            foreach (string id in recon.ResearchedTechIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (!save.UnlockedTechIds.Contains(id))
                {
                    report.All.Add(new LedgerDivergence
                    {
                        Facet = DivergenceFacet.TechNode,
                        Kind = DivergenceKind.PhantomInRecon,
                        Identity = id,
                        ExpectedFromSave = double.NaN,
                        Reconstructed = double.NaN,
                        Detail = "tech node researched in recon but not unlocked in the save"
                    });
                    phantoms++;
                }
            }

            // Counted, NOT emitted per id (see the method doc): the delta-vs-absolute
            // asymmetry makes this number expected, not a divergence.
            int unlockedNotClaimed = 0;
            foreach (string id in save.UnlockedTechIds)
            {
                if (!recon.ResearchedTechIds.Contains(id))
                    unlockedNotClaimed++;
            }

            ParsekLog.Verbose(Tag,
                $"CompareTechNodes: saveUnlocked={save.UnlockedTechIds.Count.ToString(IC)} " +
                $"reconResearched={recon.ResearchedTechIds.Count.ToString(IC)} " +
                $"phantoms={phantoms.ToString(IC)} " +
                $"unlockedNotClaimedByRecon={unlockedNotClaimed.ToString(IC)} (expected: recon is delta-only)");
        }

        // ----------------------------------------------------------------
        // Part purchases (REPORT-ONLY census; no reconstruction surface exists)
        // ----------------------------------------------------------------

        /// <summary>
        /// Reports the save's purchased-part totals. CENSUS ONLY - there is no
        /// compare half, because there is nothing on the reconstruction side to
        /// compare against.
        ///
        /// WHY NO COMPARE HALF (the CompareTechNodes one-direction lesson, applied
        /// one step earlier). The recalc models tech-node RESEARCH; part unlock is
        /// derived at patch time from KSP's own part list
        /// (KspStatePatcher.AddPurchasedPartsForTech), so no ledger-side purchase set
        /// exists and none is planned. A compare half written against a surface
        /// nothing produces is unreachable code that reads like coverage: it can
        /// never execute, so it can never catch anything, while implying the facet is
        /// diffed. CompareTechNodes earned its ONE direction by naming exactly what
        /// the recon can and cannot state; the honest answer here is that it states
        /// nothing. Add the compare half back WITH the producer, not before it.
        /// </summary>
        private static void ComparePartPurchases(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (!save.HasTechTree)
            {
                ParsekLog.Verbose(Tag, "ComparePartPurchases: save has no part-purchase facet -> skip");
                return;
            }

            ParsekLog.Verbose(Tag,
                $"ComparePartPurchases: save-side census only (saveParts=" +
                $"{save.PurchasedPartNames.Count.ToString(IC)} techNodesWithParts=" +
                $"{save.TechNodePartCounts.Count.ToString(IC)}) uncompared " +
                $"(no reconstruction part-purchase surface exists)");
        }

        // ----------------------------------------------------------------
        // Strategies (REPORT-ONLY census; no reconstruction surface exists)
        // ----------------------------------------------------------------

        /// <summary>
        /// Reports the save's active-strategy set. CENSUS ONLY - there is no compare
        /// half, for the same reason as <see cref="ComparePartPurchases"/>.
        ///
        /// StrategiesModule keeps its active set private (only GetActiveStrategyCount
        /// / IsStrategyActive are exposed) and the career-ledger plan lands strategies
        /// SHAPE-ONLY with no coverage claim, so no reconstruction ever declares an
        /// active-strategy set. A both-ways compare against a surface nothing produces
        /// is unreachable code that reads like coverage. Add it back WITH the
        /// producer.
        /// </summary>
        private static void CompareStrategies(
            CareerSaveSnapshot save, LedgerReconstructionSnapshot recon,
            LedgerDivergenceReport report)
        {
            if (!save.HasStrategySystem)
            {
                ParsekLog.Verbose(Tag, "CompareStrategies: save has no StrategySystem facet -> skip");
                return;
            }

            ParsekLog.Verbose(Tag,
                $"CompareStrategies: save-side census only (saveStrategies=" +
                $"{save.Strategies.Count.ToString(IC)} saveActive=" +
                $"{save.ActiveStrategyIds.Count.ToString(IC)}) uncompared " +
                $"(no reconstruction strategy surface exists)");
        }
    }
}
