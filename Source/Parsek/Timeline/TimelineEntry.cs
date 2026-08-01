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
        /// <summary>
        /// True when this entry came from a recording the player ARCHIVED in the
        /// Recordings tab (<see cref="Recording.Hidden"/>). Only ever set on entries
        /// the builder was explicitly asked to include (design
        /// `docs/dev/design-ui-basic-advanced.md` section 4.4), so it is false on every
        /// entry of a default build. The Timeline row draw uses it to mark a revealed
        /// row, which is what tells the player WHICH rows the Archive filter was
        /// covering.
        /// </summary>
        public bool IsArchivedRecording;
        public bool IsEffective = true;
        public bool IsPlayerAction;  // true = deliberate KSC action, false = gameplay event
        public string MilestoneId;
        public float MilestoneFundsAwarded;
        public float MilestoneRepAwarded;
        public float MilestoneScienceAwarded;
    }
}
