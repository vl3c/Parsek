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
        private readonly Dictionary<string, int> effectiveMilestoneCounts = new Dictionary<string, int>();
        private PreLedgerProgressSeed progressSeed = PreLedgerProgressSeed.NotCaptured;

        /// <summary>
        /// Installs the save's pre-ledger progress seed; the next <see cref="Reset"/> folds
        /// it in. Null is treated as not captured.
        /// </summary>
        internal void SetPreLedgerProgressSeed(PreLedgerProgressSeed seed)
        {
            progressSeed = seed ?? PreLedgerProgressSeed.NotCaptured;
        }

        /// <summary>
        /// True once the save's pre-ledger progress seed has been captured. Until then the
        /// credited set says nothing about progress that predates the ledger, so the
        /// milestone patch must not run from it.
        /// </summary>
        internal bool HasProgressSeed => progressSeed.Captured;

        /// <summary>True when the id is credited by the pre-ledger seed rather than by a ledger row.</summary>
        internal bool IsPreLedgerSeeded(string milestoneId)
        {
            return progressSeed.ContainsOneShot(milestoneId);
        }

        /// <summary>Record thresholds the pre-ledger seed adds to this id's effective count.</summary>
        internal int GetPreLedgerRecordPaidCount(string milestoneId)
        {
            return progressSeed.GetRecordPaidCount(milestoneId);
        }

        internal int PreLedgerSeedOneShotCount => progressSeed.OneShotCount;

        internal int PreLedgerSeedRecordCount => progressSeed.RecordCount;

        /// <summary>
        /// Resets all credited milestones before a recalculation walk, then folds in the
        /// pre-ledger progress seed: seeded one-shot ids start credited (they were achieved
        /// before any ledger row, so a later row for the same id is a true duplicate under
        /// the first-hit-wins rule below), and seeded record counts start the effective
        /// count of their id (record rows stay effective and add on top).
        /// </summary>
        public void Reset()
        {
            int previousCount = creditedMilestones.Count;
            creditedMilestones.Clear();
            effectiveMilestoneCounts.Clear();

            if (progressSeed.Captured)
            {
                foreach (string id in progressSeed.OneShotIds)
                    creditedMilestones.Add(id);
                foreach (var kv in progressSeed.RecordPaidCounts)
                    effectiveMilestoneCounts[kv.Key] = kv.Value;
                ParsekLog.Verbose("Milestones",
                    $"Reset: cleared {previousCount} credited milestones; pre-ledger seed credits " +
                    $"{progressSeed.OneShotCount.ToString(IC)} node(s) and " +
                    $"{progressSeed.RecordCount.ToString(IC)} record band(s)");
                return;
            }

            ParsekLog.Verbose("Milestones", $"Reset: cleared {previousCount} credited milestones");
        }

        /// <inheritdoc/>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // No pre-pass needed for milestones; walkNowUT is ignored.
            return false;
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
            bool isRepeatableRecordMilestone = IsRepeatableWorldRecordMilestone(milestoneId);

            if (!creditedMilestones.Contains(milestoneId))
            {
                action.Effective = true;
                creditedMilestones.Add(milestoneId);
                // A record id may already carry the pre-ledger seed's paid thresholds;
                // every other id starts from zero here.
                effectiveMilestoneCounts[milestoneId] =
                    (effectiveMilestoneCounts.TryGetValue(milestoneId, out int seededCount)
                        ? seededCount : 0) + 1;
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
                if (effectiveMilestoneCounts.TryGetValue(milestoneId, out int currentCount))
                    effectiveMilestoneCounts[milestoneId] = currentCount + 1;
                else
                    effectiveMilestoneCounts[milestoneId] = 1;
                // Bug #593: repeatable record milestones (RecordsSpeed/Altitude/
                // Distance) hit this branch on every recalc walk for every
                // committed record-grant action, producing 170+ identical
                // "stays effective" lines per session. Rate-limit per stable
                // GameAction.ActionId so a recalc loop walking the SAME
                // action collapses, while two distinct grants of the same
                // milestoneId in the same recording but at different UT (or
                // with different reward shapes) still log on their first
                // walk because they have different ActionIds.
                string actionIdKey = !string.IsNullOrEmpty(action.ActionId)
                    ? action.ActionId
                    : string.Format(IC, "{0}|{1}|{2}|{3}",
                        milestoneId,
                        action.RecordingId ?? "(none)",
                        action.UT.ToString("R", IC),
                        action.MilestoneFundsAwarded.ToString("R", IC));
                string key = "milestone-stays-effective-action-" + actionIdKey;
                ParsekLog.VerboseRateLimited("Milestones", key,
                    $"Repeatable record milestone '{milestoneId}' stays effective at UT={action.UT.ToString("F1", IC)}" +
                    $" (actionId={action.ActionId ?? "(none)"}," +
                    $" recording={action.RecordingId ?? "null"}," +
                    $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                    $" rep={action.MilestoneRepAwarded.ToString("F0", IC)}," +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)})," +
                    $" total credited={creditedMilestones.Count}");
            }
            else
            {
                action.Effective = false;
                action.NotCountedReason = GameActionNotCountedReason.MilestoneAlreadyAchieved;
                ParsekLog.Verbose("Milestones",
                    $"Duplicate milestone '{milestoneId}' zeroed at UT={action.UT.ToString("F1", IC)}" +
                    $" (recording={action.RecordingId ?? "null"})" +
                    (progressSeed.ContainsOneShot(milestoneId)
                        ? " - achieved before the ledger began (pre-ledger progress seed)"
                        : ""));
            }
        }

        /// <summary>
        /// True for KSP's repeatable world-record progress nodes (RecordsAltitude/
        /// RecordsDepth/RecordsSpeed/RecordsDistance). Unlike once-ever milestones these
        /// fire on every new record set during flight (essentially every physics frame of
        /// a sustained climb). Single source of truth shared with the recorder-side
        /// coalescing in <see cref="GameStateRecorder.TryCoalesceWorldRecordReward"/>, which
        /// collapses the per-frame breaks into one accumulating action so the ledger stays
        /// bounded and the recalculation engine is not driven once per frame.
        /// </summary>
        internal static bool IsRepeatableWorldRecordMilestone(string milestoneId)
        {
            return milestoneId == "RecordsAltitude"
                || milestoneId == "RecordsDepth"
                || milestoneId == "RecordsSpeed"
                || milestoneId == "RecordsDistance";
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
        /// Returns how many effective MilestoneAchievement actions survived the current walk
        /// for the given milestoneId. Repeatable Records* nodes can exceed 1; once-ever
        /// milestones are either 0 or 1.
        /// </summary>
        internal int GetEffectiveMilestoneCount(string milestoneId)
        {
            if (milestoneId == null) milestoneId = "";
            return effectiveMilestoneCounts.TryGetValue(milestoneId, out int count) ? count : 0;
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
