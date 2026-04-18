using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// First-tier milestone module. Tracks once-ever milestone achievements, except for
    /// KSP's repeatable world-record nodes (RecordsAltitude/Depth/Speed/Distance), which
    /// can award funds/rep/science multiple times while still mapping to a single
    /// progress-tree node for patching.
    ///
    /// When effective=true, the milestone's MilestoneFundsAwarded and MilestoneRepAwarded
    /// flow into the Funds and Reputation modules in the second tier.
    ///
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class MilestonesModule : IResourceModule
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private readonly HashSet<string> creditedMilestones = new HashSet<string>();

        /// <summary>
        /// Resets all credited milestones before a recalculation walk.
        /// </summary>
        public void Reset()
        {
            int previousCount = creditedMilestones.Count;
            creditedMilestones.Clear();
            ParsekLog.Verbose("Milestones", $"Reset: cleared {previousCount} credited milestones");
        }

        /// <inheritdoc/>
        public void PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // No pre-pass needed for milestones; walkNowUT is ignored.
        }

        /// <summary>
        /// Processes a single game action. Only handles MilestoneAchievement —
        /// all other action types are ignored.
        ///
        /// For MilestoneAchievement:
        ///   - First hit for any milestoneId: marks effective=true, adds to credited set
        ///   - Repeatable Records* hits after the first: stay effective, but do not grow the
        ///     credited set (the progress node still only needs to be patched to achieved once)
        ///   - Other later duplicates: marks effective=false
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action.Type != GameActionType.MilestoneAchievement)
                return;

            string milestoneId = action.MilestoneId ?? "";
            bool isRepeatableRecordMilestone =
                milestoneId == "RecordsAltitude" ||
                milestoneId == "RecordsDepth" ||
                milestoneId == "RecordsSpeed" ||
                milestoneId == "RecordsDistance";

            if (!creditedMilestones.Contains(milestoneId))
            {
                action.Effective = true;
                creditedMilestones.Add(milestoneId);
                ParsekLog.Verbose("Milestones",
                    $"Credited milestone '{milestoneId}' at UT={action.UT.ToString("F1", IC)}" +
                    $" (recording={action.RecordingId ?? "null"}," +
                    $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                    $" rep={action.MilestoneRepAwarded.ToString("F0", IC)}," +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)})," +
                    $" total credited={creditedMilestones.Count}");
            }
            else if (isRepeatableRecordMilestone)
            {
                action.Effective = true;
                ParsekLog.Verbose("Milestones",
                    $"Repeatable record milestone '{milestoneId}' stays effective at UT={action.UT.ToString("F1", IC)}" +
                    $" (recording={action.RecordingId ?? "null"}," +
                    $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                    $" rep={action.MilestoneRepAwarded.ToString("F0", IC)}," +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)})," +
                    $" total credited={creditedMilestones.Count}");
            }
            else
            {
                action.Effective = false;
                ParsekLog.Verbose("Milestones",
                    $"Duplicate milestone '{milestoneId}' zeroed at UT={action.UT.ToString("F1", IC)}" +
                    $" (recording={action.RecordingId ?? "null"})");
            }
        }

        /// <summary>
        /// Returns whether the given milestoneId has been credited in the current walk.
        /// </summary>
        internal bool IsMilestoneCredited(string milestoneId)
        {
            return creditedMilestones.Contains(milestoneId);
        }

        /// <summary>
        /// Returns the number of milestones credited in the current walk.
        /// </summary>
        internal int GetCreditedCount()
        {
            return creditedMilestones.Count;
        }

        /// <summary>
        /// Returns a copy of the credited milestone IDs for patching use.
        /// The returned set can be iterated without affecting module state.
        /// </summary>
        internal HashSet<string> GetCreditedMilestoneIds()
        {
            return new HashSet<string>(creditedMilestones);
        }

        public void PostWalk() { }
    }
}
