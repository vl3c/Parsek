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
        Vessel
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
        Consistency
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
        /// per-identity facets (SubjectScience / Facility / Contract / Milestone),
        /// phantoms, and uncorroborated (pid-only) recovery Consistency entries.
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
        /// </summary>
        internal static bool IsAlwaysHard(LedgerDivergence d)
        {
            switch (d.Facet)
            {
                case DivergenceFacet.Funds:
                case DivergenceFacet.SciencePool:
                case DivergenceFacet.Reputation:
                    return true;
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
