using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Discriminator for timeline entries — covers every event source.
    /// 29 types total: 5 recording lifecycle, 23 game actions (1:1 with GameActionType),
    /// 1 legacy (pre-ledger events).
    /// </summary>
    public enum TimelineEntryType
    {
        // Recording lifecycle (5)
        RecordingStart,
        VesselSpawn,
        CrewDeath,
        UnfinishedFlightSeparation,
        Separation,

        // Game actions (23, 1:1 with GameActionType)
        ScienceEarning,
        ScienceSpending,
        FundsEarning,
        FundsSpending,
        ReputationEarning,
        ReputationPenalty,
        MilestoneAchievement,
        ContractAccept,
        ContractComplete,
        ContractFail,
        ContractCancel,
        KerbalAssignment,
        KerbalHire,
        KerbalRescue,
        KerbalStandIn,
        FacilityUpgrade,
        FacilityDestruction,
        FacilityRepair,
        StrategyActivate,
        StrategyDeactivate,
        FundsInitial,
        ScienceInitial,
        ReputationInitial,

        // Legacy (1)
        LegacyEvent
    }

    /// <summary>
    /// Which system produced this timeline entry.
    /// </summary>
    public enum TimelineSource
    {
        Recording,
        GameAction,
        Legacy
    }

    /// <summary>
    /// Significance tier for timeline filtering.
    /// T1 = Overview (default), T2 = Detail.
    /// Entry is visible if its tier &lt;= the selected display level.
    /// </summary>
    public enum SignificanceTier
    {
        T1 = 1,
        T2 = 2
    }

    /// <summary>
    /// Normalized view object for a single timeline entry.
    /// Constructed on demand by <see cref="TimelineBuilder"/>, never serialized.
    /// </summary>
    public class TimelineEntry
    {
        public double UT;
        public TimelineEntryType Type;
        public string DisplayText;
        public TimelineSource Source;
        public SignificanceTier Tier;
        public Color DisplayColor;
        public string RecordingId;
        public string VesselName;
        public bool IsEffective = true;
        public bool IsPlayerAction;  // true = deliberate KSC action, false = gameplay event
        public string MilestoneId;
        public float MilestoneFundsAwarded;
        public float MilestoneRepAwarded;
        public float MilestoneScienceAwarded;
        /// <summary>
        /// The career subject this row belongs to, from the ledger action type (never the
        /// display type): the Timeline's Career view shows one category at a time. None on
        /// recording and legacy rows and on ledger rows outside the five categories.
        /// </summary>
        public TimelineCareerCategory CareerCategory;
        /// <summary>
        /// The row's subject inside <see cref="CareerCategory"/> (contract id, strategy id,
        /// facility id, milestone id or tech node id), the key
        /// <c>TimelineWindowUI.ScrollToCareerSubject</c> scrolls to. Null when None.
        /// </summary>
        public string CareerSubjectId;
        /// <summary>
        /// The ledger row this entry was built from (the compacted copy for a folded facility
        /// row), or null on recording and legacy rows. Runtime only, like the entry itself:
        /// the row hover reads the row's own fields (contract terms, the walk's not-counted
        /// reason, the hold key) instead of re-deriving them from the display text.
        /// </summary>
        public GameAction Action;
        /// <summary>
        /// ContractAccept rows only: the first ContractComplete row of the same contract at or
        /// after the accept, or null when the ledger has none yet. The hover names its rewards.
        /// </summary>
        public GameAction PairedContractComplete;
    }
}
