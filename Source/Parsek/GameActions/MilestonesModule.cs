using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// First-tier milestone module. Tracks once-ever milestone achievements, except for
    /// KSP's repeatable world-record nodes (RecordsAltitude/Depth/Speed/Distance), which
    /// pay once per reward threshold while still mapping to a single progress-tree node for
    /// patching: each threshold is credited once per career, earliest crossing first
    /// (<see cref="RecordThresholdBook"/>, design 7.8 and section 3.15 step 4).
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
        private readonly RecordThresholdBook recordBook = new RecordThresholdBook();
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
        /// the first-hit-wins rule below), and a seeded record count credits that node's
        /// first thresholds (positions 1..count, paid before any ledger row).
        /// </summary>
        public void Reset()
        {
            int previousCount = creditedMilestones.Count;
            creditedMilestones.Clear();
            effectiveMilestoneCounts.Clear();
            recordBook.Reset(progressSeed.Captured ? progressSeed.RecordPaidCounts : null);

            if (progressSeed.Captured)
            {
                foreach (string id in progressSeed.OneShotIds)
                    creditedMilestones.Add(id);
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
        /// Processes a single game action. Only handles MilestoneAchievement -
        /// all other action types are ignored.
        ///
        /// For a one-shot milestone: the first hit for a milestoneId is effective and joins
        /// the credited set; a later hit is a duplicate (effective=false,
        /// <see cref="GameActionNotCountedReason.MilestoneAlreadyAchieved"/>).
        ///
        /// For a repeatable Records* row (<see cref="ProcessRecordRow"/>): each threshold the
        /// row crossed is credited once per career, earliest crossing in walk order first, and
        /// an overlap zeroes a threshold only on rewind evidence (<see cref="RecordThresholdBook"/>),
        /// so one timeline walks what stock paid. A row with no threshold counted earns
        /// nothing (<see cref="GameActionNotCountedReason.RecordThresholdsAlreadyCredited"/>); a
        /// row with some counted earns only those.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action.Type != GameActionType.MilestoneAchievement)
                return;

            string milestoneId = action.MilestoneId ?? "";
            if (IsRepeatableWorldRecordMilestone(milestoneId))
            {
                ProcessRecordRow(action, milestoneId);
                return;
            }

            if (!creditedMilestones.Contains(milestoneId))
            {
                action.Effective = true;
                creditedMilestones.Add(milestoneId);
                effectiveMilestoneCounts[milestoneId] =
                    (effectiveMilestoneCounts.TryGetValue(milestoneId, out int seededCount)
                        ? seededCount : 0) + 1;
                ParsekLog.Verbose("Milestones",
                    $"Credited milestone '{milestoneId}' at UT={action.UT.ToString("F1", IC)}" +
                    $" (recording={action.RecordingId ?? "null"}," +
                    $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                    $" rep={action.MilestoneRepAwarded.ToString("F0", IC)}," +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)}" +
                    $"), total credited={creditedMilestones.Count}");
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
        /// A world-record row: the book (<see cref="RecordThresholdBook"/>) says which of its
        /// thresholds are new in this walk. All new: counted in full, exactly as before
        /// step 4. None new: not counted. Some new: counted, earning only the new thresholds'
        /// share (<see cref="GameAction.RecordPartlyCredited"/>). A row that stands for no
        /// threshold (stock's completion event) stays counted with nothing to pay. The node's
        /// effective count is the seed plus the counted first payments, so the record band
        /// patch rebuilds the band the career was paid for, never one a re-crossing doubled.
        /// </summary>
        private void ProcessRecordRow(GameAction action, string milestoneId)
        {
            RecordThresholdBook.RowCredit credit = recordBook.Credit(action);
            action.RecordThresholdsTotal = credit.Count;
            action.RecordThresholdsCredited = credit.NewCount;
            effectiveMilestoneCounts[milestoneId] = recordBook.GetCreditedCount(milestoneId);

            // Rate-limited per stable ActionId (bug #593): every recalc walk re-decides every
            // committed record row, and a steady loop must collapse to one line per row.
            string actionIdKey = !string.IsNullOrEmpty(action.ActionId)
                ? action.ActionId
                : string.Format(IC, "{0}|{1}|{2}|{3}",
                    milestoneId,
                    action.RecordingId ?? "(none)",
                    action.UT.ToString("R", IC),
                    action.MilestoneFundsAwarded.ToString("R", IC));

            if (credit.NoneNew)
            {
                action.Effective = false;
                action.NotCountedReason = GameActionNotCountedReason.RecordThresholdsAlreadyCredited;
                ParsekLog.VerboseRateLimited("Milestones", "record-threshold-repeat-" + actionIdKey,
                    $"Record row '{milestoneId}' zeroed at UT={action.UT.ToString("F1", IC)}: every threshold " +
                    $"it crossed was credited earlier on timeline (actionId={action.ActionId ?? "(none)"}," +
                    $" recording={action.RecordingId ?? "null"}, cseq={action.CaptureSequence.ToString(IC)}," +
                    $" {RecordThresholdBook.Describe(credit)}," +
                    $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                    $" rep={action.MilestoneRepAwarded.ToString("F1", IC)}," +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)})," +
                    $" credited thresholds={effectiveMilestoneCounts[milestoneId].ToString(IC)}");
                return;
            }

            action.Effective = true;
            if (!credit.AllNew)
            {
                action.RecordPartlyCredited = true;
                action.RecordCreditedFunds = credit.Funds;
                action.RecordCreditedRep = credit.Rep;
                action.RecordCreditedScience = credit.Science;
                ParsekLog.VerboseRateLimited("Milestones", "record-threshold-partial-" + actionIdKey,
                    $"Record row '{milestoneId}' partly credited at UT={action.UT.ToString("F1", IC)}:" +
                    $" {credit.NewCount.ToString(IC)} of {credit.Count.ToString(IC)} threshold(s) new" +
                    $" (actionId={action.ActionId ?? "(none)"}, recording={action.RecordingId ?? "null"}," +
                    $" cseq={action.CaptureSequence.ToString(IC)}, {RecordThresholdBook.Describe(credit)}," +
                    $" captured funds={action.MilestoneFundsAwarded.ToString("F0", IC)}" +
                    $" rep={action.MilestoneRepAwarded.ToString("F1", IC)}" +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)};" +
                    $" credited funds={credit.Funds.ToString("F0", IC)}" +
                    $" rep={credit.Rep.ToString("F1", IC)} sci={credit.Science.ToString("F1", IC)})," +
                    $" credited thresholds={effectiveMilestoneCounts[milestoneId].ToString(IC)}");
            }

            if (creditedMilestones.Add(milestoneId))
            {
                ParsekLog.Verbose("Milestones",
                    $"Credited milestone '{milestoneId}' at UT={action.UT.ToString("F1", IC)}" +
                    $" (recording={action.RecordingId ?? "null"}," +
                    $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                    $" rep={action.MilestoneRepAwarded.ToString("F0", IC)}," +
                    $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)}" +
                    $", thresholds={credit.Count.ToString(IC)}, {RecordThresholdBook.Describe(credit)}" +
                    $"), total credited={creditedMilestones.Count}");
                return;
            }

            string key = "milestone-stays-effective-action-" + actionIdKey;
            ParsekLog.VerboseRateLimited("Milestones", key,
                $"Repeatable record milestone '{milestoneId}' stays effective at UT={action.UT.ToString("F1", IC)}" +
                $" (actionId={action.ActionId ?? "(none)"}," +
                $" recording={action.RecordingId ?? "null"}," +
                $" funds={action.MilestoneFundsAwarded.ToString("F0", IC)}," +
                $" rep={action.MilestoneRepAwarded.ToString("F0", IC)}," +
                $" sci={action.MilestoneScienceAwarded.ToString("F1", IC)}," +
                $" thresholds={credit.Count.ToString(IC)}, {RecordThresholdBook.Describe(credit)})," +
                $" total credited={creditedMilestones.Count}");
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
        /// How many stock reward awards a world-record row stands for
        /// (<see cref="GameAction.MilestoneRecordThresholds"/>, never below 0). A row that does
        /// not name its thresholds (<see cref="GameAction.MilestoneRecordBand"/> null) takes
        /// this many consecutive positions in the walk (<see cref="RecordThresholdBook"/>).
        /// Shared with <see cref="PreLedgerProgressSeed.Capture"/>, which subtracts what the
        /// ledger already owns from the live band; on a career never rewound that equals the
        /// count the walk credits (re-payments aside).
        /// </summary>
        internal static int GetRepresentedRecordThresholds(GameAction action)
        {
            if (action == null)
                return 0;
            return action.MilestoneRecordThresholds < 0 ? 0 : action.MilestoneRecordThresholds;
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
        /// Returns the effective hit count of the given milestoneId in the current walk. For a
        /// once-ever milestone that is 0 or 1. For a repeatable Records* node it is the
        /// pre-ledger seed plus the counted first payments
        /// (<see cref="RecordThresholdBook.GetCreditedCount"/>), not the number of rows and not
        /// the sum of their thresholds: a threshold two timelines crossed counts once, and a
        /// re-payment adds nothing.
        /// </summary>
        internal int GetEffectiveMilestoneCount(string milestoneId)
        {
            if (milestoneId == null) milestoneId = "";
            if (IsRepeatableWorldRecordMilestone(milestoneId))
                return recordBook.GetCreditedCount(milestoneId);
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
