using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    // =====================================================================
    // Data model for the in-game ledger ground-truth verification harness.
    //
    // Layer A (pure, headless-testable): the types here plus CareerSaveParser
    // and LedgerGroundTruthDiff (in sibling files). No Unity scene access, no
    // live singletons. Operate on ConfigNode + plain structs so the parse/diff
    // logic is unit-tested in Source/Parsek.Tests with synthetic .sfs fixtures.
    //
    // See docs/dev/design-ledger-groundtruth-harness.md for the full spec.
    // =====================================================================

    /// <summary>
    /// Parsed ground-truth save (S): KSP's own independent serialization of
    /// career state at the current UT, read straight off disk with zero ledger
    /// involvement. Each facet is independently optional: a missing SCENARIO
    /// sets the matching HasX flag false / leaves the collection empty, never
    /// throws.
    /// </summary>
    internal sealed class CareerSaveSnapshot
    {
        /// <summary>False when the GAME/FLIGHTSTATE shape was unrecognizable.</summary>
        public bool Parsed;

        /// <summary>Why <see cref="Parsed"/> is false (for Skip messages); "" when parsed.</summary>
        public string Reason = "";

        public bool HasFunds;
        public double Funds;

        public bool HasScience;
        public double SciencePool;

        public bool HasRep;
        public double Reputation;

        /// <summary>subjectId -> cumulative earned science.</summary>
        public Dictionary<string, double> SubjectScience = new Dictionary<string, double>();

        /// <summary>"SpaceCenter/LaunchPad" -> normalized fraction level (0..1).</summary>
        public Dictionary<string, double> FacilityLevelFrac = new Dictionary<string, double>();

        /// <summary>CONTRACT guids with state==Active.</summary>
        public HashSet<string> ActiveContractGuids = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every CONTRACT guid regardless of state (phantom test).</summary>
        public HashSet<string> ContractGuidsAllStates = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>ProgressTracking nodes carrying a `completed` field (qualified + bare ids).</summary>
        public HashSet<string> CompletedMilestoneIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Every ProgressTracking milestone node id (qualified + bare; phantom test).</summary>
        public HashSet<string> AllMilestoneIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>FLIGHTSTATE &gt; VESSEL entries.</summary>
        public List<SaveVessel> Vessels = new List<SaveVessel>();

        /// <summary>
        /// True when the save carried a GAME &gt; ROSTER node. ROSTER is a DIRECT
        /// child of GAME, not a SCENARIO, so it is unreachable through the
        /// SCENARIO lookup every other facet uses.
        /// </summary>
        public bool HasRoster;

        /// <summary>GAME &gt; ROSTER &gt; KERBAL entries, in file order.</summary>
        public List<SaveKerbal> Roster = new List<SaveKerbal>();

        /// <summary>
        /// True when the save carried a ResearchAndDevelopment SCENARIO (the node
        /// holding both the Tech unlock set and the per-node part purchases).
        /// Independent of <see cref="HasScience"/>, which additionally requires a
        /// parsable `sci` value.
        /// </summary>
        public bool HasTechTree;

        /// <summary>
        /// Tech node ids the save lists as unlocked (one ResearchAndDevelopment
        /// &gt; Tech node per unlocked node; KSP writes no node for a locked one).
        /// </summary>
        public HashSet<string> UnlockedTechIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Every purchased part name, unioned across all Tech nodes. Purchases are
        /// the REPEATED `part` values inside a Tech node.
        /// </summary>
        public HashSet<string> PurchasedPartNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>techId -&gt; number of purchased parts recorded under that node.</summary>
        public Dictionary<string, int> TechNodePartCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>True when the save carried a StrategySystem SCENARIO.</summary>
        public bool HasStrategySystem;

        /// <summary>
        /// StrategySystem &gt; STRATEGIES &gt; STRATEGY entries, in file order. An
        /// EMPTY STRATEGIES block is the normal shape for a career whose strategy
        /// was deactivated (KSP removes the node) and is NOT a parse failure.
        /// </summary>
        public List<SaveStrategy> Strategies = new List<SaveStrategy>();

        /// <summary>Names of the strategies the save reports as active.</summary>
        public HashSet<string> ActiveStrategyIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Per-kerbal career-log entries parsed from ROSTER &gt; KERBAL &gt; CAREER_LOG. The
        /// unit stock derives experience from - XP itself is never stored, it is recomputed
        /// from this log, so the log IS the ground truth for the KerbalXp facet.
        /// </summary>
        public Dictionary<string, HashSet<KerbalCareerLogEntry>> KerbalCareerLog =
            new Dictionary<string, HashSet<KerbalCareerLogEntry>>(StringComparer.Ordinal);
    }

    /// <summary>A kerbal parsed from GAME &gt; ROSTER &gt; KERBAL.</summary>
    internal struct SaveKerbal
    {
        /// <summary>KERBAL.name ("Bill Kerman"); the roster's identity key.</summary>
        public string Name;

        /// <summary>KERBAL.gender ("Male" / "Female").</summary>
        public string Gender;

        /// <summary>KERBAL.type ("Crew" / "Applicant" / "Tourist" / "Unowned").</summary>
        public string Type;

        /// <summary>KERBAL.trait ("Pilot" / "Engineer" / "Scientist" / "Tourist").</summary>
        public string Trait;

        /// <summary>KERBAL.state ("Available" / "Assigned" / "Dead" / "Missing").</summary>
        public string State;
    }

    /// <summary>
    /// A strategy parsed from StrategySystem &gt; STRATEGIES &gt; STRATEGY.
    ///
    /// SHAPE NOTE (measured against the one real sample on this machine): a
    /// STRATEGY node carries `name`, `date` and `factor` plus EFFECT children.
    /// Stock KSP writes no `isActive` field - PRESENCE in the STRATEGIES block is
    /// the active signal, and a deactivated strategy is removed from the save
    /// entirely. <see cref="IsActive"/> therefore defaults to true on presence,
    /// and only an EXPLICIT `isActive = False` value (defensive: a mod or a future
    /// KSP could write one) turns it off.
    /// </summary>
    internal struct SaveStrategy
    {
        /// <summary>STRATEGY.name ("PatentsLicensingCfg").</summary>
        public string Name;

        /// <summary>True unless an explicit `isActive = False` value was present.</summary>
        public bool IsActive;

        /// <summary>STRATEGY.date (activation UT); 0 when absent.</summary>
        public double ActivatedUT;

        /// <summary>STRATEGY.factor (the commitment slider, 0..1); 0 when absent.</summary>
        public double Factor;
    }

    /// <summary>A vessel parsed from FLIGHTSTATE &gt; VESSEL.</summary>
    internal struct SaveVessel
    {
        /// <summary>VESSEL.pid (Guid string; launch-unique correlator).</summary>
        public string Pid;

        /// <summary>VESSEL.persistentId (craft-baked, NOT launch-unique).</summary>
        public uint PersistentId;

        public string Name;
        public string Type;

        /// <summary>resource name -> summed amount across all parts.</summary>
        public Dictionary<string, double> ResourceTotals;
    }

    /// <summary>
    /// Reconstruction snapshot: values produced by the recalc modules. Built in
    /// Layer B (the in-game harness) from the module accessors; consumed by the
    /// Layer A diff. The pool readers use the RAW running values
    /// (GetRunningBalance / GetRunningScience / GetRunningRep), not the Available
    /// readers (see design data-model reader-choice note).
    /// </summary>
    internal sealed class LedgerReconstructionSnapshot
    {
        public bool HasFunds;
        public double Funds;

        public bool HasScience;
        public double SciencePool;

        public bool HasRep;
        public double Reputation;

        /// <summary>subjectId -> CreditedTotal (Science.GetAllSubjects()).</summary>
        public Dictionary<string, double> SubjectScience = new Dictionary<string, double>();

        /// <summary>facilityId -> 1-based level (Facilities.GetAllFacilities()).</summary>
        public Dictionary<string, int> FacilityLevel = new Dictionary<string, int>();

        /// <summary>Active contract guids (Contracts.GetActiveContractIds()).</summary>
        public HashSet<string> ActiveContractGuids = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Credited milestone ids (Milestones.GetCreditedMilestoneIds()).</summary>
        public HashSet<string> CreditedMilestoneIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Recovery credits: ledger FundsEarning + Recovery (vessel facet).</summary>
        public List<RecoveryCredit> RecoveryCredits = new List<RecoveryCredit>();

        /// <summary>
        /// True when the reconstruction has a ROSTER surface to compare at all (the
        /// KerbalsModule resolved). False leaves the roster facet UNCOMPARED: the
        /// diff then reports the save-side census only and never invents a
        /// reconstruction the recalc does not produce.
        /// </summary>
        public bool HasRosterSurface;

        /// <summary>
        /// Kerbals the ledger believes IT created (KerbalsModule.LedgerCreatedKerbals).
        /// DELTA-only: this is not the full roster, so the meaningful direction is
        /// "created in recon but absent from the save".
        /// </summary>
        public HashSet<string> LedgerCreatedKerbals = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Kerbals the ledger holds permanently reserved (a KerbalsModule permanent
        /// reservation means dead, never freed).
        /// </summary>
        public HashSet<string> PermanentlyGoneKerbals = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True when the reconstruction has a researched-tech surface (the ledger's
        /// affordable ScienceSpending rows). DELTA-only, see
        /// <see cref="ResearchedTechIds"/>.
        /// </summary>
        public bool HasTechSurface;

        /// <summary>
        /// Tech node ids the ledger's affordable ScienceSpending rows claim were
        /// researched. DELTA-only: a mixed-history career unlocked nodes before
        /// Parsek was installed and those rows do not exist, so only the "claimed
        /// by recon but not unlocked in the save" direction is meaningful.
        /// </summary>
        public HashSet<string> ResearchedTechIds = new HashSet<string>(StringComparer.Ordinal);

        // NO purchased-part or active-strategy surface, deliberately: the recalc
        // models tech-node RESEARCH (part unlock is derived at patch time from KSP's
        // own part list, KspStatePatcher.AddPurchasedPartsForTech) and StrategiesModule
        // keeps its active set private. Both facets are save-side CENSUS ONLY in
        // LedgerGroundTruthDiff; a field nothing ever fills would only make an
        // unreachable compare half look reachable.

        /// <summary>
        /// Per-kerbal career-log entries the reconstruction credits, read off the
        /// KerbalsModule accumulator.
        /// </summary>
        public Dictionary<string, HashSet<KerbalCareerLogEntry>> KerbalCareerLog =
            new Dictionary<string, HashSet<KerbalCareerLogEntry>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// A vessel-recovery funds credit reconstructed from the ledger. Vessel
    /// identity is resolved from the Recording (GameAction has no VesselName):
    /// guid is the preferred correlator; a bare persistentId match is NOT proof
    /// of identity (craft-baked-pid caveat).
    /// </summary>
    internal struct RecoveryCredit
    {
        public string RecordingId;

        /// <summary>From Recording.VesselName.</summary>
        public string VesselName;

        /// <summary>From Recording.RecordedVesselGuid (launch-unique; preferred correlator). May be null/empty.</summary>
        public string VesselGuid;

        /// <summary>From Recording.VesselPersistentId (craft-baked, NOT launch-unique). 0 = unset.</summary>
        public uint VesselPid;

        /// <summary>action.FundsAwarded.</summary>
        public double Amount;
    }

    /// <summary>Which career facet a divergence belongs to.</summary>
    internal enum DivergenceFacet
    {
        Funds,
        SciencePool,
        Reputation,
        SubjectScience,
        Facility,
        Contract,
        Milestone,
        Vessel,

        /// <summary>
        /// Per-kerbal career-log entries (P9a). REPORT-ONLY: deliberately absent from
        /// <see cref="LedgerDivergenceReport.IsAlwaysHard"/>, matching the SubjectScience
        /// posture. A kerbal's career log legitimately carries entries the ledger never saw
        /// (pre-Parsek flights, stand-ins, mod-written entries), so a mismatch here is
        /// information rather than corruption until a scenario proves otherwise.
        /// </summary>
        KerbalXp,

        /// <summary>GAME &gt; ROSTER kerbals (report-only).</summary>
        Roster,

        /// <summary>ResearchAndDevelopment Tech unlock set (report-only).</summary>
        TechNode

        // NO PartPurchase / Strategy members: those two facets are save-side census
        // only (no reconstruction surface produces either set), so no divergence can
        // ever be tagged with them. Add a member back WITH the producer.
    }

    /// <summary>What kind of disagreement a divergence represents.</summary>
    internal enum DivergenceKind
    {
        /// <summary>Both sides have the identity but the values differ.</summary>
        ValueMismatch,

        /// <summary>Recon credits an identity that is absent from the save.</summary>
        PhantomInRecon,

        /// <summary>The save has an identity the recon is missing.</summary>
        MissingInRecon,

        /// <summary>Cross-subsystem consistency violation (e.g. recovery-credit vs present vessel).</summary>
        Consistency,

        /// <summary>
        /// A seeded-pool scalar where the reconstruction (the RAW running balance) runs
        /// ABOVE the live save value while no authoritative time-travel context is active.
        /// The production drawdown guard (<see cref="KspStatePatcher.ApplyDrawdownGuard"/>)
        /// UPLIFT-clamps such a patch DOWN to the live value, so the live career is NOT
        /// over-credited: the gap is the documented missing-spending-channel surplus on a
        /// mixed-history career, not save corruption. Report-only by default (promoted under
        /// strict). A DOWNWARD divergence stays <see cref="ValueMismatch"/> (hard) because the
        /// guard does NOT raise the live value to meet a lower recon -- that is the
        /// save-corruption class the harness exists to catch.
        /// </summary>
        UpliftClampedExpected
    }

    /// <summary>A single comparison disagreement between save and reconstruction.</summary>
    internal struct LedgerDivergence
    {
        public DivergenceFacet Facet;
        public DivergenceKind Kind;

        /// <summary>subjectId / facilityId / contractGuid / vessel id; "" for scalars.</summary>
        public string Identity;

        /// <summary>Expected value from the save; NaN when N/A.</summary>
        public double ExpectedFromSave;

        /// <summary>Reconstructed value; NaN when N/A.</summary>
        public double Reconstructed;

        /// <summary>Human-readable, grep-stable detail.</summary>
        public string Detail;

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "facet={0} kind={1} id={2} expected={3} recon={4} detail={5}",
                Facet,
                Kind,
                string.IsNullOrEmpty(Identity) ? "(scalar)" : Identity,
                FormatValue(ExpectedFromSave),
                FormatValue(Reconstructed),
                Detail ?? "");
        }

        internal static string FormatValue(double v)
        {
            return double.IsNaN(v) ? "n/a" : v.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The full set of comparison disagreements plus helpers for selecting the
    /// hard-failure subset and formatting a stable multi-line report.
    /// </summary>
    internal sealed class LedgerDivergenceReport
    {
        public List<LedgerDivergence> All = new List<LedgerDivergence>();

        /// <summary>How many facets the diff actually compared (save.HasX true / collection considered).</summary>
        public int FacetsCompared;

        /// <summary>
        /// The subset of divergences that fail the test.
        ///
        /// Always-hard:
        ///   - the seeded pools: Funds / SciencePool / Reputation;
        ///   - guid-corroborated vessel-recovery Consistency divergences.
        ///
        /// When <paramref name="strict"/> is true, ALSO promotes the report-only
        /// per-identity facets (SubjectScience / Facility / Contract / Milestone /
        /// KerbalXp / Roster / TechNode / PartPurchase / Strategy), phantoms, and
        /// uncorroborated (pid-only) recovery Consistency entries.
        /// </summary>
        internal List<LedgerDivergence> HardFailures(bool strict)
        {
            var hard = new List<LedgerDivergence>();
            foreach (var d in All)
            {
                if (IsAlwaysHard(d))
                {
                    hard.Add(d);
                    continue;
                }

                // Always-hard entries already continued above, so any divergence
                // reaching here is report-only; strict promotes the whole set.
                if (strict)
                    hard.Add(d);
            }
            return hard;
        }

        /// <summary>
        /// A divergence is hard regardless of strictness when it is a seeded-pool
        /// scalar (funds/science/rep) or a guid-corroborated vessel-recovery
        /// Consistency violation. Identified via the grep-stable detail marker
        /// "guidCorroborated=true" written by the diff.
        ///
        /// EXCEPTION: a seeded-pool scalar tagged <see cref="DivergenceKind.UpliftClampedExpected"/>
        /// is report-only. That kind means the reconstruction (RAW running balance) runs ABOVE
        /// the live save with no authoritative time-travel context, so the production drawdown
        /// guard UPLIFT-clamps the patch DOWN to live and the career is never over-credited -- the
        /// expected missing-channel surplus on a mixed-history career, not corruption. A DOWNWARD
        /// seeded-pool divergence stays <see cref="DivergenceKind.ValueMismatch"/> and remains hard.
        /// </summary>
        internal static bool IsAlwaysHard(LedgerDivergence d)
        {
            switch (d.Facet)
            {
                case DivergenceFacet.Funds:
                case DivergenceFacet.SciencePool:
                case DivergenceFacet.Reputation:
                    return d.Kind != DivergenceKind.UpliftClampedExpected;
                case DivergenceFacet.Vessel:
                    return d.Kind == DivergenceKind.Consistency
                        && d.Detail != null
                        && d.Detail.IndexOf("guidCorroborated=true", StringComparison.Ordinal) >= 0;
                default:
                    return false;
            }
        }

        /// <summary>Multi-line stable string for the assert message + log (one line per divergence).</summary>
        internal string Format()
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "LedgerDivergenceReport: total={0} facetsCompared={1}",
                All.Count,
                FacetsCompared);
            for (int i = 0; i < All.Count; i++)
            {
                sb.Append('\n');
                sb.Append(All[i].ToString());
            }
            return sb.ToString();
        }
    }

    /// <summary>Per-facet numeric tolerances for the scalar / per-subject comparisons.</summary>
    internal struct FacetTolerances
    {
        public double Funds;
        public double SciencePool;
        public double Reputation;
        public double Subject;

        internal static FacetTolerances Default
        {
            get
            {
                return new FacetTolerances
                {
                    Funds = 1.0,
                    SciencePool = 0.1,
                    Reputation = 0.1,
                    Subject = 0.1
                };
            }
        }
    }
}
