using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// First-tier milestone module. Tracks once-ever milestone achievements.
    /// Each milestoneId can be credited exactly once — the chronologically first
    /// recording gets effective=true, all later duplicates get effective=false.
    ///
    /// When effective=true, the milestone's MilestoneFundsAwarded and MilestoneRepAwarded
    /// flow into the Funds and Reputation modules in the second tier.
    ///
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class MilestonesModule : IResourceModule
    {
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

        /// <summary>
        /// Processes a single game action. Only handles MilestoneAchievement —
        /// all other action types are ignored.
        ///
        /// For MilestoneAchievement:
        ///   - If milestoneId not yet credited: marks effective=true, adds to credited set
        ///   - If milestoneId already credited: marks effective=false (duplicate zeroed)
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action.Type != GameActionType.MilestoneAchievement)
                return;

            string milestoneId = action.MilestoneId;

            if (!creditedMilestones.Contains(milestoneId))
            {
                action.Effective = true;
                creditedMilestones.Add(milestoneId);
                ParsekLog.Verbose("Milestones",
                    $"Credited milestone '{milestoneId}' at UT={action.UT:F1}" +
                    $" (recording={action.RecordingId ?? "null"}," +
                    $" funds={action.MilestoneFundsAwarded:F0}," +
                    $" rep={action.MilestoneRepAwarded:F0})," +
                    $" total credited={creditedMilestones.Count}");
            }
            else
            {
                action.Effective = false;
                ParsekLog.Verbose("Milestones",
                    $"Duplicate milestone '{milestoneId}' zeroed at UT={action.UT:F1}" +
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
    }
}
