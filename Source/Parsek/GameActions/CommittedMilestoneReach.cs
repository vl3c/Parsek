using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Parsek
{
    /// <summary>
    /// Keeps stock from paying a once-ever world-first that the committed timeline already
    /// credits (todo WORLD-FIRST-REEARNED-AFTER-REWIND-PAID-AGAIN).
    ///
    /// <para>Stock pays a world-first only while its progress node is not complete: every
    /// once-ever handler (decompiled KSP 1.12.5 <c>FlagPlant.OnFlagPlant</c>,
    /// <c>SurfaceEVA.OnGoEVA</c>, <c>CelestialBodyLanding</c>, <c>CelestialBodyOrbit</c>, ...)
    /// runs <c>if (!IsComplete) { Complete(); AwardProgressStandard(...); }</c>. After a
    /// rewind the loaded save has the node unreached and the committed row lies ahead of the
    /// clock. When the clock passes that row, <see cref="KspStatePatcher.PatchMilestones"/>
    /// would set the node achieved, but every patch is deferred while a recorder lives
    /// (<c>LedgerOrchestrator.GetKspPatchDeferralReason</c>) and a flight at 1x runs no recalc
    /// at all. A later flight that re-earns the node is then paid by stock again, the walk
    /// zeroes its row as a duplicate (<see cref="MilestonesModule"/>: first in UT order wins)
    /// and the stock pool reference keeps the payout as a leak, so the player is paid twice.</para>
    ///
    /// <para>This pass marks such a node complete the way <see cref="KspStatePatcher.PatchMilestones"/>
    /// does (the private <c>reached</c> / <c>complete</c> fields, no <c>Complete()</c>, no
    /// GameEvents, no reward) as soon as the clock is at or past a committed row for it, and
    /// it runs whether or not the KSP patch is deferred. It is add-only: it never clears a
    /// node, so a live flight's own uncommitted progress is never undone. The committed set
    /// is the walk's own input (<see cref="EffectiveState.ComputeELS"/>) at the clock, minus
    /// the rows of a live Re-Fly session's suppressed subtree (the origin being re-flown:
    /// a merge tombstones its milestone rows, so the attempt must be able to earn the node
    /// itself).</para>
    ///
    /// <para>World records (design 7.9, section 3.15 step 4): stock pays a record threshold
    /// while the node's band is below it, so a band a rewind's authoritative patch lowered is
    /// paid again by a re-climbing flight. The same pass raises each record node's band to
    /// the committed count at the clock (the pre-ledger seed plus the counted first payments of
    /// the committed record rows at or before it, counted by the walk's own
    /// <see cref="RecordThresholdBook"/>) when that is above the count stock's live state
    /// says it already paid. Add-only too: a band is never lowered, nothing is paid and no
    /// event fires.</para>
    /// </summary>
    internal static class CommittedMilestoneReach
    {
        internal const string Tag = "CommittedMilestones";
        private const int IdSampleCap = 10;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Test seam: the progress tree the live pass patches (null reads stock's).</summary>
        internal static Func<ProgressTree> ProgressTreeProviderForTesting;

        /// <summary>Test seam: the recording ids the live pass excludes (null reads the Re-Fly marker).</summary>
        internal static Func<ICollection<string>> SuppressedRecordingIdsProviderForTesting;

        internal static void ResetForTesting()
        {
            ProgressTreeProviderForTesting = null;
            SuppressedRecordingIdsProviderForTesting = null;
            SeedRecordCountsProviderForTesting = null;
        }

        /// <summary>The outcome of one pass over the progress tree.</summary>
        internal struct ReachCounts
        {
            internal int Marked;
            internal int AlreadyComplete;
            internal int SkippedRecordNodes;
            /// <summary>Record nodes whose band this pass raised.</summary>
            internal int RecordBandsRaised;
            /// <summary>Record nodes whose live band was already at or above the committed count.</summary>
            internal int RecordBandsAtOrAbove;
        }

        /// <summary>
        /// Pure: true for a ledger row this pass treats as a committed once-ever earning:
        /// a <see cref="GameActionType.MilestoneAchievement"/> with an id that is not one of
        /// stock's repeatable world-record nodes.
        /// </summary>
        internal static bool IsOnceEverMilestoneRow(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.MilestoneAchievement
                && !string.IsNullOrEmpty(action.MilestoneId)
                && !MilestonesModule.IsRepeatableWorldRecordMilestone(action.MilestoneId);
        }

        /// <summary>
        /// Pure: true for a committed world-record row that stands for at least one threshold
        /// (it names them, or its count is above 0). A completion row (no threshold) is not.
        /// </summary>
        internal static bool IsRecordThresholdRow(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.MilestoneAchievement
                && MilestonesModule.IsRepeatableWorldRecordMilestone(action.MilestoneId ?? "")
                && ((action.MilestoneRecordBand != null && action.MilestoneRecordBand.Count > 0)
                    || MilestonesModule.GetRepresentedRecordThresholds(action) > 0);
        }

        /// <summary>Pure: a row the mirror acts on (a once-ever row or a record threshold row),
        /// so the clock cursor fires when the clock passes one.</summary>
        internal static bool IsMirroredMilestoneRow(GameAction action)
        {
            return IsOnceEverMilestoneRow(action) || IsRecordThresholdRow(action);
        }

        /// <summary>
        /// Pure: the committed band of each world-record node at <paramref name="clockUT"/>:
        /// the pre-ledger seed (<paramref name="seedCounts"/>) plus the counted first payments of
        /// the record rows of <paramref name="actions"/> at or before the clock, walked in the
        /// walk's own order through <see cref="RecordThresholdBook"/> so the mirror and the
        /// replay count alike. Rows tagged to a recording in
        /// <paramref name="excludedRecordingIds"/> (a live Re-Fly session's suppressed
        /// subtree) are left out and counted in <paramref name="excludedRows"/>. Only ids with
        /// a row in the window are returned.
        /// </summary>
        internal static Dictionary<string, int> CollectCommittedRecordCountsAtOrBefore(
            IEnumerable<GameAction> actions, double clockUT,
            ICollection<string> excludedRecordingIds,
            IReadOnlyDictionary<string, int> seedCounts,
            out int excludedRows)
        {
            excludedRows = 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (actions == null || double.IsNaN(clockUT))
                return counts;

            var rows = new List<GameAction>();
            foreach (var a in actions)
            {
                if (!IsRecordThresholdRow(a) || a.UT > clockUT)
                    continue;
                if (excludedRecordingIds != null && !string.IsNullOrEmpty(a.RecordingId)
                    && excludedRecordingIds.Contains(a.RecordingId))
                {
                    excludedRows++;
                    continue;
                }
                rows.Add(a);
            }
            if (rows.Count == 0)
                return counts;

            // Stable, like the walk's own sort (equal keys keep the ledger order).
            rows = rows.OrderBy(a => a, Comparer<GameAction>.Create(RecalculationEngine.CompareWalkOrder))
                .ToList();
            var book = new RecordThresholdBook();
            book.Reset(seedCounts);
            for (int i = 0; i < rows.Count; i++)
                book.Credit(rows[i]);
            for (int i = 0; i < rows.Count; i++)
            {
                string id = rows[i].MilestoneId;
                if (!counts.ContainsKey(id))
                    counts[id] = book.GetCreditedCount(id);
            }
            return counts;
        }

        /// <summary>
        /// Pure: the once-ever milestone ids <paramref name="actions"/> credit at or before
        /// <paramref name="clockUT"/>, skipping rows tagged to a recording in
        /// <paramref name="excludedRecordingIds"/>. The earliest row of an id is the one the
        /// walk credits, so an id is in the set exactly when the walk at this clock credits it
        /// from a row. <paramref name="excludedRows"/> counts the rows the exclusion dropped.
        /// </summary>
        internal static HashSet<string> CollectCreditedAtOrBefore(
            IEnumerable<GameAction> actions, double clockUT,
            ICollection<string> excludedRecordingIds, out int excludedRows)
        {
            excludedRows = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (actions == null || double.IsNaN(clockUT))
                return ids;
            foreach (var a in actions)
            {
                if (!IsOnceEverMilestoneRow(a) || a.UT > clockUT)
                    continue;
                if (excludedRecordingIds != null && !string.IsNullOrEmpty(a.RecordingId)
                    && excludedRecordingIds.Contains(a.RecordingId))
                {
                    excludedRows++;
                    continue;
                }
                ids.Add(a.MilestoneId);
            }
            return ids;
        }

        /// <summary>
        /// Pure: the UT of the earliest once-ever milestone row or world-record threshold row
        /// strictly after <paramref name="afterUT"/>, or +infinity when there is none.
        /// </summary>
        internal static double NextRowUTAfter(IEnumerable<GameAction> actions, double afterUT)
        {
            double next = double.PositiveInfinity;
            if (actions == null)
                return next;
            foreach (var a in actions)
            {
                if (!IsMirroredMilestoneRow(a))
                    continue;
                if (a.UT > afterUT && a.UT < next)
                    next = a.UT;
            }
            return next;
        }

        /// <summary>
        /// Pure: the clock a pass may trust. A walk with a real cutoff is at that cutoff
        /// (the time jump's target, the clock cursor's now). Otherwise the live clock, except
        /// while OnLoad runs or a rewind's UT adjustment is pending (both read a clock that is
        /// not the loaded game's yet). NaN, a non-positive value or the walk-everything
        /// sentinel is not a clock: the pass marks nothing (it cannot be undone by itself,
        /// so it never runs on a guess).
        /// </summary>
        internal static double ResolveClockUT(
            double? walkUtCutoff, bool onLoadInProgress, bool rewindUTAdjustmentPending,
            Func<double> liveClockUT)
        {
            if (walkUtCutoff.HasValue && IsRealClock(walkUtCutoff.Value))
                return walkUtCutoff.Value;
            if (onLoadInProgress || rewindUTAdjustmentPending || liveClockUT == null)
                return double.NaN;
            double live = liveClockUT();
            return IsRealClock(live) ? live : double.NaN;
        }

        internal static bool IsRealClock(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut) && ut > 0.0
                && !KerbalsModule.IsNoTimeFilterCutoff(ut);
        }

        /// <summary>
        /// Marks every once-ever node of <paramref name="tree"/> whose qualified id
        /// (bare at the top level, "Parent/NodeId" below it, as
        /// <see cref="KspStatePatcher.PatchProgressNodeTree"/> qualifies it) is in
        /// <paramref name="creditedIds"/> and is not complete: reached and complete are set
        /// through the private fields, nothing else is touched and no event fires. A complete
        /// node is left as it is, and nothing is ever cleared. Repeatable world-record nodes
        /// are skipped. <paramref name="markedIds"/> receives the ids it marked.
        /// </summary>
        internal static void MarkCreditedNodes(
            ProgressTree tree, ISet<string> creditedIds,
            FieldInfo reachedField, FieldInfo completeField, string pathPrefix,
            ref ReachCounts counts, List<string> markedIds)
        {
            if (tree == null || creditedIds == null || creditedIds.Count == 0
                || reachedField == null || completeField == null)
                return;

            for (int i = 0; i < tree.Count; i++)
            {
                ProgressNode node = tree[i];
                if (node == null) continue;

                string nodeId = node.Id ?? "";
                string qualifiedId = string.IsNullOrEmpty(pathPrefix)
                    ? nodeId
                    : pathPrefix + "/" + nodeId;

                if (creditedIds.Contains(qualifiedId))
                {
                    if (KspStatePatcher.IsRepeatableRecordType(node))
                    {
                        counts.SkippedRecordNodes++;
                    }
                    else if (node.IsComplete)
                    {
                        counts.AlreadyComplete++;
                    }
                    else
                    {
                        reachedField.SetValue(node, true);
                        completeField.SetValue(node, true);
                        counts.Marked++;
                        markedIds?.Add(qualifiedId);
                    }
                }

                if (node.Subtree != null && node.Subtree.Count > 0)
                {
                    MarkCreditedNodes(node.Subtree, creditedIds, reachedField, completeField,
                        qualifiedId, ref counts, markedIds);
                }
            }
        }

        /// <summary>
        /// Raises every repeatable world-record node of <paramref name="tree"/> whose
        /// qualified id has a committed count in <paramref name="committedCounts"/> above its
        /// live paid count (<see cref="KspStatePatcher.TryRaiseRepeatableRecordBand"/>). Never
        /// lowers a band. <paramref name="raisedIds"/> receives "id:live->committed" for each
        /// raise.
        /// </summary>
        internal static void RaiseCommittedRecordBands(
            ProgressTree tree, IDictionary<string, int> committedCounts,
            FieldInfo reachedField, FieldInfo completeField, string pathPrefix,
            ref ReachCounts counts, List<string> raisedIds)
        {
            if (tree == null || committedCounts == null || committedCounts.Count == 0
                || reachedField == null || completeField == null)
                return;

            for (int i = 0; i < tree.Count; i++)
            {
                ProgressNode node = tree[i];
                if (node == null) continue;

                string nodeId = node.Id ?? "";
                string qualifiedId = string.IsNullOrEmpty(pathPrefix)
                    ? nodeId
                    : pathPrefix + "/" + nodeId;

                if (committedCounts.TryGetValue(qualifiedId, out int committed)
                    && KspStatePatcher.IsRepeatableRecordType(node))
                {
                    if (KspStatePatcher.TryRaiseRepeatableRecordBand(node, committed, qualifiedId,
                        reachedField, completeField, out int livePaid, out bool raised))
                    {
                        if (raised)
                        {
                            counts.RecordBandsRaised++;
                            raisedIds?.Add(qualifiedId + ":" + livePaid.ToString(IC) + "->" + committed.ToString(IC));
                        }
                        else
                        {
                            counts.RecordBandsAtOrAbove++;
                        }
                    }
                }

                if (node.Subtree != null && node.Subtree.Count > 0)
                {
                    RaiseCommittedRecordBands(node.Subtree, committedCounts, reachedField, completeField,
                        qualifiedId, ref counts, raisedIds);
                }
            }
        }

        /// <summary>
        /// The live pass: marks stock's progress nodes for every once-ever world-first the
        /// committed ledger credits at or before <paramref name="clockUT"/>. Called from the
        /// deferred branch of the recalculation (a patch held back by a live or pending tree)
        /// and from the flight clock cursor when the clock passes a committed milestone row.
        /// One summary line per pass: Info when it marked a node, otherwise once per change.
        /// </summary>
        internal static int Apply(IEnumerable<GameAction> actions, double clockUT, string reason)
        {
            string why = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
            if (!IsRealClock(clockUT))
            {
                ParsekLog.VerboseOnChange(Tag, "apply-skip|" + why, "clock-not-ready",
                    $"Apply skipped: no trusted clock (reason={why}, " +
                    $"clock={clockUT.ToString("R", IC)}) - the next pass with a clock marks the nodes");
                return 0;
            }

            ICollection<string> suppressed = ResolveSuppressedRecordingIds();
            // Materialized once: both collections below walk it.
            List<GameAction> rows = actions as List<GameAction>
                ?? (actions != null ? new List<GameAction>(actions) : new List<GameAction>());
            HashSet<string> credited = CollectCreditedAtOrBefore(rows, clockUT, suppressed,
                out int excludedOnceEver);
            Dictionary<string, int> recordCounts = CollectCommittedRecordCountsAtOrBefore(
                rows, clockUT, suppressed, ResolveSeedRecordCounts(), out int excludedRecord);
            int excludedRows = excludedOnceEver + excludedRecord;
            if (credited.Count == 0 && recordCounts.Count == 0)
            {
                ParsekLog.VerboseOnChange(Tag, "apply-none|" + why,
                    "none|" + excludedRows.ToString(IC),
                    $"Apply: no committed once-ever milestone or record row at or before clock={clockUT.ToString("F1", IC)} " +
                    $"(reason={why}, reFlySuppressedRows={excludedRows.ToString(IC)})");
                return 0;
            }

            ProgressTree tree = ReadProgressTree(out string treeSkip);
            if (tree == null)
            {
                ParsekLog.VerboseOnChange(Tag, "apply-skip|" + why, treeSkip,
                    $"Apply skipped: {treeSkip} (reason={why}, credited={credited.Count.ToString(IC)}, " +
                    $"recordNodes={recordCounts.Count.ToString(IC)})");
                return 0;
            }

            FieldInfo reachedField = typeof(ProgressNode).GetField("reached",
                BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo completeField = typeof(ProgressNode).GetField("complete",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (reachedField == null || completeField == null)
            {
                ParsekLog.Warn(Tag,
                    "Apply: could not find ProgressNode reached/complete fields via reflection " +
                    $"(reached={reachedField != null}, complete={completeField != null}) - stock may pay " +
                    "a world-first the committed timeline already credits");
                return 0;
            }

            var counts = new ReachCounts();
            var markedIds = new List<string>();
            var raisedIds = new List<string>();
            using (SuppressionGuard.ResourcesAndReplay())
            {
                MarkCreditedNodes(tree, credited, reachedField, completeField, "", ref counts, markedIds);
                RaiseCommittedRecordBands(tree, recordCounts, reachedField, completeField, "", ref counts, raisedIds);
            }

            int notInTree = credited.Count - counts.Marked - counts.AlreadyComplete - counts.SkippedRecordNodes;
            string summary =
                $"marked={counts.Marked.ToString(IC)} alreadyComplete={counts.AlreadyComplete.ToString(IC)} " +
                $"credited={credited.Count.ToString(IC)} notInTree={notInTree.ToString(IC)} " +
                $"recordNodes={counts.SkippedRecordNodes.ToString(IC)} " +
                $"recordBandsRaised={counts.RecordBandsRaised.ToString(IC)} " +
                $"recordBandsAtOrAbove={counts.RecordBandsAtOrAbove.ToString(IC)} " +
                $"reFlySuppressedRows={excludedRows.ToString(IC)}";
            if (counts.Marked > 0 || counts.RecordBandsRaised > 0)
            {
                ParsekLog.Info(Tag,
                    $"Apply: marked {counts.Marked.ToString(IC)} stock progress node(s) complete without " +
                    $"reward and raised {counts.RecordBandsRaised.ToString(IC)} record band(s) without reward, " +
                    $"the committed timeline credits them at or before clock={clockUT.ToString("F1", IC)} " +
                    $"(reason={why}; {summary}) " +
                    "ids=[" + KspStatePatcher.ComposeBoundedIdentitySample(markedIds, IdSampleCap) + "] " +
                    "records=[" + KspStatePatcher.ComposeBoundedIdentitySample(raisedIds, IdSampleCap) + "]");
            }
            else
            {
                ParsekLog.VerboseOnChange(Tag, "apply-noop|" + why, summary,
                    $"Apply: nothing to mark at clock={clockUT.ToString("F1", IC)} (reason={why}; {summary})");
            }
            return counts.Marked + counts.RecordBandsRaised;
        }

        /// <summary>Test seam: the pre-ledger record counts the live pass starts from (null
        /// reads the ledger's captured seed).</summary>
        internal static Func<IReadOnlyDictionary<string, int>> SeedRecordCountsProviderForTesting;

        private static IReadOnlyDictionary<string, int> ResolveSeedRecordCounts()
        {
            var seam = SeedRecordCountsProviderForTesting;
            if (seam != null)
                return seam();
            PreLedgerProgressSeed seed = Ledger.ProgressSeed;
            return seed != null && seed.Captured ? seed.RecordPaidCounts : null;
        }

        private static ICollection<string> ResolveSuppressedRecordingIds()
        {
            var seam = SuppressedRecordingIdsProviderForTesting;
            if (seam != null)
                return seam();
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
                return null;
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
                return null;
            var suppressed = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            return suppressed as ICollection<string>
                ?? new HashSet<string>(suppressed, StringComparer.Ordinal);
        }

        private static ProgressTree ReadProgressTree(out string skipReason)
        {
            skipReason = null;
            var seam = ProgressTreeProviderForTesting;
            if (seam != null)
            {
                ProgressTree t = seam();
                if (t == null) skipReason = "test progress tree is null";
                return t;
            }
            if (ProgressTracking.Instance == null)
            {
                skipReason = "ProgressTracking.Instance is null";
                return null;
            }
            ProgressTree tree = ProgressTracking.Instance.achievementTree;
            if (tree == null)
                skipReason = "achievementTree is null";
            return tree;
        }
    }

    /// <summary>
    /// Runs <see cref="CommittedMilestoneReach.Apply"/> when the flight clock passes a
    /// committed once-ever milestone row or world-record threshold row. The flight scene has no ledger clock cursor (the
    /// Space Center and Tracking Station run one that recalculates), and a recalculation
    /// there is deferred while a recorder lives, so without this a world-first the committed
    /// timeline earned at UT 100 stays payable in stock for a flight that re-earns it at
    /// UT 120 at 1x. Per frame it costs a cached lookup and two comparisons; the first valid
    /// frame and a backward clock only reseed.
    /// </summary>
    internal sealed class CommittedMilestoneClockCursor
    {
        internal const double BackwardEpsilonSeconds = 0.05;

        private readonly string reason;
        private readonly Func<double, double> nextRowUTAfter;
        private readonly Action<double, string> apply;

        private double lastUT = double.NaN;
        private int cachedLedgerVersion = int.MinValue;
        private int cachedTombstoneVersion = int.MinValue;
        private double cachedAfterUT = double.NaN;
        private double cachedNextUT = double.PositiveInfinity;

        /// <param name="reasonPrefix">Scene tag, e.g. "flight" gives reason "flight-clock".</param>
        /// <param name="nextRowUTAfter">Test seam; defaults to the effective ledger, cached
        /// on the ledger and tombstone versions.</param>
        /// <param name="apply">Test seam; defaults to <see cref="CommittedMilestoneReach.Apply"/>
        /// over the effective ledger.</param>
        internal CommittedMilestoneClockCursor(
            string reasonPrefix,
            Func<double, double> nextRowUTAfter = null,
            Action<double, string> apply = null)
        {
            reason = (string.IsNullOrEmpty(reasonPrefix) ? "scene" : reasonPrefix) + "-clock";
            this.nextRowUTAfter = nextRowUTAfter;
            this.apply = apply
                ?? ((ut, why) => CommittedMilestoneReach.Apply(EffectiveState.ComputeELS(), ut, why));
        }

        internal double LastUT => lastUT;

        /// <summary>
        /// Advances the cursor to <paramref name="currentUT"/>. Returns true when the clock
        /// passed a committed once-ever milestone row or world-record threshold row and the
        /// pass ran.
        /// </summary>
        internal bool Advance(double currentUT)
        {
            if (!CommittedMilestoneReach.IsRealClock(currentUT))
                return false;

            if (double.IsNaN(lastUT) || currentUT < lastUT - BackwardEpsilonSeconds)
            {
                lastUT = currentUT;
                return false;
            }

            double next = GetNextRowUTAfter(lastUT);
            if (currentUT < next)
                return false;

            ParsekLog.Verbose(CommittedMilestoneReach.Tag,
                $"Clock cursor ({reason}): clock {currentUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"passed a committed milestone row at UT {next.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(last={lastUT.ToString("F1", CultureInfo.InvariantCulture)})");
            apply(currentUT, reason);
            lastUT = currentUT;
            return true;
        }

        private double GetNextRowUTAfter(double afterUT)
        {
            if (nextRowUTAfter != null)
                return nextRowUTAfter(afterUT);

            int ledgerVersion = Ledger.StateVersion;
            var scenario = ParsekScenario.Instance;
            int tombstoneVersion = ReferenceEquals(null, scenario) ? 0 : scenario.TombstoneStateVersion;
            if (ledgerVersion != cachedLedgerVersion || tombstoneVersion != cachedTombstoneVersion
                || !(afterUT == cachedAfterUT))
            {
                cachedLedgerVersion = ledgerVersion;
                cachedTombstoneVersion = tombstoneVersion;
                cachedAfterUT = afterUT;
                cachedNextUT = CommittedMilestoneReach.NextRowUTAfter(EffectiveState.ComputeELS(), afterUT);
            }
            return cachedNextUT;
        }
    }
}
