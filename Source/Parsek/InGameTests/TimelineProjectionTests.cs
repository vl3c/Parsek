using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// D15 `timeline-projection`: the Timeline is a read-only projection of the effective
    /// ledger (timeline design section 3.4). Builds the timeline from the exact inputs the
    /// Timeline window passes (ERS, ELS, milestones, the legacy visibility predicate, the
    /// live game mode) and checks it against the ELS it was built from: every ELS action
    /// is either represented by exactly one GameAction row or accounted for by one of the
    /// design's named exclusions, no row exists without a ledger action behind it, the
    /// list is UT-sorted, and ineffective actions never render at tier T1.
    ///
    /// <para>The exclusion set is written out here from the design rather than read from
    /// the builder, so a builder that silently widens its filter (drops a row the design
    /// says must show) fails as a missing action instead of passing.</para>
    /// </summary>
    public class TimelineProjectionTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // TimelineBuilder's documented fold windows (design section 3.4): one stock
        // facility event touches each building, and same-milestone rows merge.
        private const double FacilityFoldWindowSeconds = 1.0;
        private const double MilestoneFoldWindowSeconds = 0.1;

        [InGameTest(Category = "Timeline",
            Description = "Timeline rows project the effective ledger: every ELS action appears once or is a named exclusion, UT-sorted, ineffective rows demoted")]
        public void TimelineProjectsEffectiveLedger()
        {
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            IReadOnlyList<GameAction> els = EffectiveState.ComputeELS();
            if (els == null || els.Count == 0)
            {
                InGameAssert.Skip("the effective ledger is empty; this test needs a career save with ledger actions");
                return;
            }

            Game.Modes? mode = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode : (Game.Modes?)null;
            List<TimelineEntry> timeline = TimelineBuilder.Build(
                ers,
                els,
                MilestoneStore.Milestones,
                GameStateStore.IsEventVisibleToCurrentTimeline,
                mode);
            InGameAssert.IsNotNull(timeline, "TimelineBuilder.Build returned null");

            // 1. UT order over the whole list (all three collectors).
            int orderBreaks = 0;
            for (int i = 1; i < timeline.Count; i++)
            {
                if (timeline[i].UT < timeline[i - 1].UT)
                    orderBreaks++;
            }

            // 2. Expected action rows, after the design's named exclusions.
            var vesselNameById = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ers.Count; i++)
            {
                if (ers[i] != null && !string.IsNullOrEmpty(ers[i].RecordingId))
                    vesselNameById[ers[i].RecordingId] = ers[i].VesselName;
            }
            HashSet<string> evaBranchKeys = TimelineBuilder.BuildEvaBranchKeys(ers);

            var expected = new List<GameAction>();
            int excludedRoute = 0, excludedModeSeed = 0, excludedZeroSpend = 0, excludedEvaCrew = 0;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (IsDesignLedgerOnlyType(a.Type)) { excludedRoute++; continue; }
                if (!TimelineBuilder.IsInitialResourceSeedVisibleInMode(a.Type, mode)) { excludedModeSeed++; continue; }
                if (a.Type == GameActionType.FundsSpending && a.FundsSpent <= 0f) { excludedZeroSpend++; continue; }
                if (a.Type == GameActionType.KerbalAssignment && IsEvaCrewNoise(a, vesselNameById, evaBranchKeys))
                {
                    excludedEvaCrew++;
                    continue;
                }
                expected.Add(a);
            }

            // 3. Match every GameAction row to exactly one expected action.
            var matched = new bool[expected.Count];
            var matchedTo = new Dictionary<TimelineEntry, GameAction>();
            int actionRows = 0;
            var unmatchedRows = new List<string>();
            for (int e = 0; e < timeline.Count; e++)
            {
                TimelineEntry entry = timeline[e];
                if (entry == null || entry.Source != TimelineSource.GameAction) continue;
                actionRows++;
                int hit = -1;
                for (int x = 0; x < expected.Count; x++)
                {
                    if (matched[x]) continue;
                    GameAction a = expected[x];
                    if (a.UT == entry.UT
                        && TimelineEntryDisplay.MapGameActionType(a.Type) == entry.Type
                        && string.Equals(a.RecordingId ?? "", entry.RecordingId ?? "", StringComparison.Ordinal)
                        && string.Equals(a.MilestoneId ?? "", entry.MilestoneId ?? "", StringComparison.Ordinal))
                    {
                        hit = x;
                        break;
                    }
                }
                if (hit < 0 && entry.Type == TimelineEntryType.MilestoneAchievement
                    && !string.IsNullOrEmpty(entry.MilestoneId))
                {
                    // A compacted milestone row carries merged recording metadata, so its
                    // anchor action is found by milestone id inside the fold window.
                    for (int x = 0; x < expected.Count; x++)
                    {
                        if (matched[x]) continue;
                        GameAction a = expected[x];
                        if (a.Type == GameActionType.MilestoneAchievement
                            && string.Equals(a.MilestoneId, entry.MilestoneId, StringComparison.Ordinal)
                            && Math.Abs(a.UT - entry.UT) <= MilestoneFoldWindowSeconds)
                        {
                            hit = x;
                            break;
                        }
                    }
                }
                if (hit < 0)
                {
                    if (unmatchedRows.Count < 8)
                        unmatchedRows.Add(string.Format(IC, "{0}@{1:F2} '{2}'", entry.Type, entry.UT, entry.DisplayText));
                    continue;
                }
                matched[hit] = true;
                matchedTo[entry] = expected[hit];
            }

            // 4. Unmatched expected actions must be a documented fold into a kept row.
            int foldedFacility = 0, foldedMilestone = 0;
            var missing = new List<string>();
            for (int x = 0; x < expected.Count; x++)
            {
                if (matched[x]) continue;
                GameAction a = expected[x];
                if (IsFoldedIntoKeptRow(a, timeline))
                {
                    if (a.Type == GameActionType.MilestoneAchievement) foldedMilestone++;
                    else foldedFacility++;
                    continue;
                }
                if (missing.Count < 8)
                    missing.Add(string.Format(IC, "{0}@{1:F2} id={2}", a.Type, a.UT, a.ActionId ?? "?"));
                else
                    missing.Add("...");
            }

            // 5. Ineffective actions never render at T1, and effectiveness carries through.
            int demotionBreaks = 0;
            // 6. Every action row is humanized into its own bucket: LegacyEvent is the
            // legacy collector's type only, and a row whose text is the raw enum name
            // fell through every display arm.
            int unhumanizedRows = 0;
            var unhumanized = new List<string>();
            foreach (var kv in matchedTo)
            {
                if (kv.Key.Type == TimelineEntryType.LegacyEvent
                    || string.IsNullOrEmpty(kv.Key.DisplayText)
                    || kv.Key.DisplayText == kv.Value.Type.ToString())
                {
                    unhumanizedRows++;
                    if (unhumanized.Count < 8)
                        unhumanized.Add(string.Format(IC, "{0}@{1:F2} as {2} '{3}'",
                            kv.Value.Type, kv.Value.UT, kv.Key.Type, kv.Key.DisplayText));
                }
            }
            foreach (var kv in matchedTo)
            {
                if (!kv.Value.Effective && kv.Key.Tier == SignificanceTier.T1) demotionBreaks++;
                // A compacted milestone row ORs its members' effectiveness, so only
                // uncompacted row types must carry their action's flag verbatim.
                if (kv.Key.Type != TimelineEntryType.MilestoneAchievement
                    && kv.Key.IsEffective != kv.Value.Effective) demotionBreaks++;
            }

            ParsekLog.Info("TimelineProjection",
                string.Format(IC,
                    "timeline projection: els={0} expected={1} entries={2} actionRows={3} matched={4} " +
                    "foldedFacility={5} foldedMilestone={6} excludedRoute={7} excludedModeSeed={8} " +
                    "excludedZeroSpend={9} excludedEvaCrew={10} missing={11} unmatchedRows={12} " +
                    "orderBreaks={13} demotionBreaks={14} unhumanizedRows={15} mode={16}",
                    els.Count, expected.Count, timeline.Count, actionRows, matchedTo.Count,
                    foldedFacility, foldedMilestone, excludedRoute, excludedModeSeed,
                    excludedZeroSpend, excludedEvaCrew, missing.Count, unmatchedRows.Count,
                    orderBreaks, demotionBreaks, unhumanizedRows, mode.HasValue ? mode.Value.ToString() : "none"));

            InGameAssert.AreEqual(0, orderBreaks, "timeline entries must be sorted by UT");
            InGameAssert.IsTrue(unmatchedRows.Count == 0,
                "timeline rows with no effective-ledger action behind them (or a duplicate row): "
                + string.Join("; ", unmatchedRows.ToArray()));
            InGameAssert.IsTrue(missing.Count == 0,
                "effective-ledger actions with no timeline row and no named exclusion: "
                + string.Join("; ", missing.ToArray()));
            InGameAssert.AreEqual(0, demotionBreaks,
                "an ineffective ledger action rendered at T1 or lost its effectiveness flag");
            InGameAssert.IsTrue(unhumanizedRows == 0,
                "action rows with no display arm of their own (LegacyEvent type or raw enum text): "
                + string.Join("; ", unhumanized.ToArray()));
            InGameAssert.IsTrue(matchedTo.Count > 0,
                "no ledger action matched a timeline row; the projection check would be vacuous");
        }

        /// <summary>
        /// Design section 3.3: "the route action types have no timeline entry". Every
        /// other type, KerbalExperience included, must render as its own row.
        /// </summary>
        internal static bool IsDesignLedgerOnlyType(GameActionType type)
        {
            return Logistics.RouteLedgerRetire.IsRouteActionType(type);
        }

        private static bool IsEvaCrewNoise(
            GameAction a, Dictionary<string, string> vesselNameById, HashSet<string> evaBranchKeys)
        {
            if (string.IsNullOrEmpty(a.RecordingId)) return false;
            string vesselName;
            if (!string.IsNullOrEmpty(a.KerbalName)
                && vesselNameById.TryGetValue(a.RecordingId, out vesselName)
                && vesselName == a.KerbalName)
                return true;
            return evaBranchKeys.Contains(TimelineBuilder.EncodeEvaBranchKey(a.RecordingId, a.UT));
        }

        private static bool IsFoldedIntoKeptRow(GameAction a, List<TimelineEntry> timeline)
        {
            bool facility = a.Type == GameActionType.FacilityDestruction || a.Type == GameActionType.FacilityRepair;
            bool milestone = a.Type == GameActionType.MilestoneAchievement && !string.IsNullOrEmpty(a.MilestoneId);
            if (!facility && !milestone) return false;

            TimelineEntryType rowType = TimelineEntryDisplay.MapGameActionType(a.Type);
            for (int i = 0; i < timeline.Count; i++)
            {
                TimelineEntry entry = timeline[i];
                if (entry == null || entry.Source != TimelineSource.GameAction || entry.Type != rowType)
                    continue;
                if (facility
                    && Math.Abs(entry.UT - a.UT) <= FacilityFoldWindowSeconds
                    && string.Equals(entry.RecordingId ?? "", a.RecordingId ?? "", StringComparison.Ordinal))
                    return true;
                if (milestone
                    && Math.Abs(entry.UT - a.UT) <= MilestoneFoldWindowSeconds
                    && string.Equals(entry.MilestoneId, a.MilestoneId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
