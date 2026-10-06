using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekScenario
    {
        /// <summary>
        /// Truncates a live active-tree recording to the current quickload-resume UT so
        /// post-load recording can continue from the restored timeline without appending
        /// stale samples/events from the pre-load future. Returns true when any payload was
        /// removed or clipped.
        /// </summary>
        internal static bool TrimRecordingPastUT(Recording rec, double cutoffUT)
        {
            if (rec == null || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
                return false;

            bool mutated = false;

            mutated |= RemoveItemsPastUT(rec.Points, cutoffUT, p => p.ut);
            mutated |= TrimOrbitSegmentsPastUT(rec.OrbitSegments, cutoffUT);
            mutated |= RemoveItemsPastUT(rec.PartEvents, cutoffUT, e => e.ut);
            mutated |= RemoveItemsPastUT(rec.FlagEvents, cutoffUT, e => e.ut);
            mutated |= RemoveItemsPastUT(rec.SegmentEvents, cutoffUT, e => e.ut);
            mutated |= TrimTrackSectionsPastUT(rec.TrackSections, cutoffUT);

            if (!double.IsNaN(rec.ExplicitStartUT) && rec.ExplicitStartUT > cutoffUT)
            {
                rec.ExplicitStartUT = cutoffUT;
                mutated = true;
            }

            if (double.IsNaN(rec.ExplicitEndUT) || rec.ExplicitEndUT > cutoffUT)
            {
                rec.ExplicitEndUT = cutoffUT;
                mutated = true;
            }

            if (mutated)
                rec.MarkFilesDirty();

            return mutated;
        }

        internal static bool TrimRecordingTreePastUT(RecordingTree tree, double cutoffUT)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0
                || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
            {
                return false;
            }

            bool mutated = false;
            int trimmedCount = 0;
            HashSet<string> futureOnlyIds = CollectFutureOnlyRecordingIds(tree, cutoffUT);
            int recordingCountBeforeTrim = tree.Recordings.Count;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (TrimRecordingPastUT(rec, cutoffUT))
                {
                    mutated = true;
                    trimmedCount++;
                }
            }

            if (mutated || (futureOnlyIds != null && futureOnlyIds.Count > 0))
            {
                int prunedRecordings = PruneFutureOnlyRecordings(tree, futureOnlyIds);
                int prunedBranchPoints = RemoveEmptyBranchPoints(tree);
                if (prunedRecordings > 0 || prunedBranchPoints > 0)
                    mutated = true;

                tree.RebuildBackgroundMap();
                ParsekLog.Info("Scenario",
                    $"Quickload tree trim: tree='{tree.TreeName}' cutoffUT={cutoffUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"trimmedRecordings={trimmedCount}/{recordingCountBeforeTrim} " +
                    $"prunedFutureRecordings={prunedRecordings} prunedBranchPoints={prunedBranchPoints} " +
                    $"backgroundEntries={tree.BackgroundMap.Count}");
            }

            return mutated;
        }

        /// <summary>
        /// Bug #610: scope of the quickload-resume tail trim. Tree-wide is correct
        /// for F9 quickload — the world rewound, every recording's post-cutoff data
        /// is stale and future-only recordings never existed at the resume UT.
        /// Re-Fly is different: the splice has already restored post-RP recordings
        /// that represent OTHER vessels' continued timelines and the re-flown
        /// vessel's destroyed-fork; tree-wide trimming would clip and prune them.
        /// Only the in-place continuation target (the active rec) needs its tail
        /// trimmed so the recorder can append fresh post-cutoff data without
        /// colliding with the pre-cutoff timeline.
        /// </summary>
        internal enum QuickloadTrimScope
        {
            TreeWide = 0,
            ActiveRecOnly = 1,
        }

        /// <summary>
        /// Picks the trim scope based on whether an active Re-Fly session pins
        /// this tree. Pure function so the decision is unit-testable. The
        /// <paramref name="reason"/> string is appended to the resume-prep log
        /// line so the chosen branch is auditable from KSP.log alone (#610).
        /// </summary>
        internal static QuickloadTrimScope ChooseQuickloadTrimScope(
            string treeId,
            ReFlySessionMarker marker,
            out string reason)
        {
            if (marker == null)
            {
                reason = "no-active-refly-marker";
                return QuickloadTrimScope.TreeWide;
            }
            if (string.IsNullOrEmpty(marker.TreeId))
            {
                reason = $"refly-marker-has-no-treeid sess={marker.SessionId ?? "<no-id>"}";
                return QuickloadTrimScope.TreeWide;
            }
            if (string.IsNullOrEmpty(treeId))
            {
                reason = $"resume-tree-has-no-id markerTree={marker.TreeId}";
                return QuickloadTrimScope.TreeWide;
            }
            if (!string.Equals(marker.TreeId, treeId, StringComparison.Ordinal))
            {
                reason = $"refly-marker-tree-mismatch markerTree={marker.TreeId} resumeTree={treeId} sess={marker.SessionId ?? "<no-id>"}";
                return QuickloadTrimScope.TreeWide;
            }
            reason = $"refly-active sess={marker.SessionId ?? "<no-id>"} markerTree={marker.TreeId} originRec={marker.OriginChildRecordingId ?? "<null>"}";
            return QuickloadTrimScope.ActiveRecOnly;
        }

        // ------------------------------------------------------------------
        // Abandoned-future reconcile: the quickload-resume trim owns the
        // trimmed set's end states, tagged events and ledger rows after the
        // resume UT. Nothing outside that set and nothing still committed is
        // touched.
        // ------------------------------------------------------------------

        internal const string AbandonedFutureReason = "quickload-abandoned-future";

        /// <summary>
        /// What one quickload-resume reconcile owns, captured BEFORE the trim: the
        /// recordings the trim cuts (every recording of the tree, or only the active one
        /// under the Re-Fly scope), the future-only recordings it prunes, and each one's
        /// end UT before the trim (the trim stamps <c>ExplicitEndUT</c> = cutoff, so the
        /// post-trim end cannot tell whether a recording ended after the cutoff). A
        /// recording that is still committed is left out of every set.
        /// </summary>
        internal sealed class AbandonedFuturePlan
        {
            internal double CutoffUT;
            internal QuickloadTrimScope Scope;
            internal readonly HashSet<string> TrimmedIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> PrunedIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> PreTrimEndUT =
                new Dictionary<string, double>(StringComparer.Ordinal);
            internal readonly List<string> SkippedCommittedIds = new List<string>();
            internal readonly HashSet<string> EndStateClearedIds = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Pure: the plan for one resume. <paramref name="isStillCommitted"/> answers whether
        /// a recording id is committed history (production:
        /// <see cref="IsStillCommittedForResumeReconcile"/>); such an id is recorded in
        /// <see cref="AbandonedFuturePlan.SkippedCommittedIds"/> and owned by nothing below.
        /// The pruned set is the trim's own future-only collector, so it is exactly what
        /// <see cref="TrimRecordingTreePastUT"/> removes.
        /// </summary>
        internal static AbandonedFuturePlan BuildAbandonedFuturePlan(
            RecordingTree tree,
            string activeRecordingId,
            double cutoffUT,
            QuickloadTrimScope scope,
            Func<string, bool> isStillCommitted)
        {
            var plan = new AbandonedFuturePlan { CutoffUT = cutoffUT, Scope = scope };
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0
                || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
            {
                return plan;
            }

            var candidates = new List<string>();
            HashSet<string> futureOnlyIds = null;
            if (scope == QuickloadTrimScope.ActiveRecOnly)
            {
                if (!string.IsNullOrEmpty(activeRecordingId))
                    candidates.Add(activeRecordingId);
            }
            else
            {
                candidates.AddRange(tree.Recordings.Keys);
                futureOnlyIds = CollectFutureOnlyRecordingIds(tree, cutoffUT);
            }
            candidates.Sort(StringComparer.Ordinal);

            for (int i = 0; i < candidates.Count; i++)
            {
                string id = candidates[i];
                if (string.IsNullOrEmpty(id)
                    || !tree.Recordings.TryGetValue(id, out Recording rec)
                    || rec == null)
                {
                    continue;
                }

                if (isStillCommitted != null && isStillCommitted(id))
                {
                    plan.SkippedCommittedIds.Add(id);
                    continue;
                }

                plan.TrimmedIds.Add(id);
                plan.PreTrimEndUT[id] = rec.EndUT;
                if (futureOnlyIds != null && futureOnlyIds.Contains(id))
                    plan.PrunedIds.Add(id);
            }

            return plan;
        }

        /// <summary>
        /// Committed history (owner ruling D2: never retired by a load). A Re-Fly
        /// provisional sits in the committed list as <see cref="MergeState.NotCommitted"/>
        /// (<c>RecordingStore.AddProvisional</c>); it is the live session's recording, not
        /// committed history, so it does not count.
        /// </summary>
        internal static bool IsStillCommittedForResumeReconcile(string recordingId)
        {
            Recording committed = EffectiveState.FindCommittedRecordingByIdRaw(recordingId);
            return committed != null && committed.MergeState != MergeState.NotCommitted;
        }

        /// <summary>
        /// Pure: a trimmed recording's terminal and crew end states belong to the abandoned
        /// future when it ended strictly after the cutoff (the trim keeps a sample AT the
        /// cutoff, so an end exactly there is the quicksave's own state) and it carries any
        /// end state at all.
        /// </summary>
        internal static bool ShouldClearFutureEndState(
            double preTrimEndUT, double cutoffUT, bool hasTerminal, bool hasCrewEndStates)
        {
            if (!hasTerminal && !hasCrewEndStates)
                return false;
            if (double.IsNaN(preTrimEndUT) || double.IsNaN(cutoffUT) || double.IsInfinity(cutoffUT))
                return false;
            return preTrimEndUT > cutoffUT;
        }

        /// <summary>True when <paramref name="loadKind"/>'s policy cell for
        /// <paramref name="category"/> is <see cref="LoadReconcileAction.ReconcileAtResume"/>.</summary>
        internal static bool ShouldReconcileAtResume(LoadKind? loadKind, LoadStateCategory category)
        {
            return loadKind.HasValue
                && LoadReconcilePolicy.Decide(loadKind.Value, category).Action
                    == LoadReconcileAction.ReconcileAtResume;
        }

        /// <summary>
        /// The quickload-resume trim plus the abandoned-future reconcile, called once from
        /// <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c> when the recorder resumes
        /// a restored tree. The plan is taken before the trim; the trim itself is unchanged;
        /// then, per category whose policy cell for the arming load kind is
        /// <see cref="LoadReconcileAction.ReconcileAtResume"/>, the trimmed set's state from
        /// after the resume UT is retired. Returns whether the trim changed anything.
        /// </summary>
        internal static bool TrimAndReconcileForQuickloadResume(
            RecordingTree tree,
            Recording activeRec,
            double resumeUT,
            QuickloadTrimScope scope,
            LoadKind? loadKind,
            double loadedUT)
        {
            bool reconcileEndStates = ShouldReconcileAtResume(loadKind, LoadStateCategory.AbandonedFutureEndStates);
            bool reconcileEvents = ShouldReconcileAtResume(loadKind, LoadStateCategory.AbandonedFutureEvents);
            bool reconcileLedgerRows = ShouldReconcileAtResume(loadKind, LoadStateCategory.AbandonedFutureLedgerRows);
            bool anyCategory = reconcileEndStates || reconcileEvents || reconcileLedgerRows;

            AbandonedFuturePlan plan = anyCategory
                ? BuildAbandonedFuturePlan(
                    tree, activeRec?.RecordingId, resumeUT, scope, IsStillCommittedForResumeReconcile)
                : null;

            bool treeTrimmed = scope == QuickloadTrimScope.ActiveRecOnly
                ? TrimRecordingPastUT(activeRec, resumeUT)
                : TrimRecordingTreePastUT(tree, resumeUT);

            string treeName = tree?.TreeName ?? "<null>";
            string kindText = loadKind.HasValue ? loadKind.Value.ToString() : "none";
            if (!anyCategory)
            {
                ParsekLog.Info("Scenario",
                    $"Quickload abandoned-future reconcile skipped: tree='{treeName}' kind={kindText} " +
                    $"scope={scope} reason=policy " +
                    $"endStates={FormatResumeDecision(loadKind, LoadStateCategory.AbandonedFutureEndStates)} " +
                    $"events={FormatResumeDecision(loadKind, LoadStateCategory.AbandonedFutureEvents)} " +
                    $"ledgerRows={FormatResumeDecision(loadKind, LoadStateCategory.AbandonedFutureLedgerRows)}");
                return treeTrimmed;
            }

            if (plan.TrimmedIds.Count == 0)
            {
                ParsekLog.Info("Scenario",
                    $"Quickload abandoned-future reconcile skipped: tree='{treeName}' kind={kindText} " +
                    $"scope={scope} reason=empty-plan " +
                    $"cutoffUT={resumeUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"skippedCommitted={plan.SkippedCommittedIds.Count}");
                return treeTrimmed;
            }

            if (!double.IsNaN(loadedUT) && Math.Abs(loadedUT - resumeUT) > 1.0)
            {
                ParsekLog.Warn("Scenario",
                    "Quickload abandoned-future reconcile: the resume cutoff differs from the load's clock " +
                    $"by more than 1 s tree='{treeName}' cutoffUT={resumeUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"loadedUT={loadedUT.ToString("R", CultureInfo.InvariantCulture)}");
            }

            // End states first: the ledger step retires the KerbalAssignment summary row of
            // every recording whose end state this clears.
            int endStatesCleared = reconcileEndStates ? ClearAbandonedFutureEndStates(tree, plan) : 0;
            int eventsPurged = reconcileEvents ? PurgeAbandonedFutureEvents(plan) : 0;
            int rowsAfterCutoff = 0;
            int rowsKerbalAssignment = 0;
            int rowsPrunedRecording = 0;
            int ledgerRowsRetired = reconcileLedgerRows
                ? RetireAbandonedFutureLedgerRows(
                    plan, out rowsAfterCutoff, out rowsKerbalAssignment, out rowsPrunedRecording)
                : 0;

            if (endStatesCleared > 0)
                tree.RebuildBackgroundMap();
            if (ledgerRowsRetired > 0)
            {
                // The current-timeline recalculation keeps the KSP patch deferred while the
                // restored tree is live and uncommitted (LedgerOrchestrator.GetKspPatchDeferralReason).
                LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                    resumeUT, AbandonedFutureReason);
            }

            ParsekLog.Info("Scenario",
                $"Quickload abandoned-future reconcile: tree='{treeName}' kind={kindText} scope={scope} " +
                $"cutoffUT={resumeUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"loadedUT={loadedUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"trimmed={plan.TrimmedIds.Count} pruned={plan.PrunedIds.Count} " +
                $"endStatesCleared={endStatesCleared} eventsPurged={eventsPurged} " +
                $"ledgerRowsRetired={ledgerRowsRetired} (afterCutoff={rowsAfterCutoff} " +
                $"kerbalAssignment={rowsKerbalAssignment} prunedRecording={rowsPrunedRecording}) " +
                $"skippedCommitted={plan.SkippedCommittedIds.Count}" +
                (plan.SkippedCommittedIds.Count > 0 && plan.SkippedCommittedIds.Count < 20
                    ? $" skippedCommittedIds=[{string.Join(",", plan.SkippedCommittedIds.ToArray())}]"
                    : ""));
            return treeTrimmed;
        }

        private static string FormatResumeDecision(LoadKind? loadKind, LoadStateCategory category)
        {
            return loadKind.HasValue
                ? LoadReconcilePolicy.Decide(loadKind.Value, category).Action.ToString()
                : "none";
        }

        /// <summary>
        /// Clears the end states of every surviving trimmed recording that ended after the
        /// cutoff (<see cref="ShouldClearFutureEndState"/>) and records the ids in
        /// <see cref="AbandonedFuturePlan.EndStateClearedIds"/>. Returns the count.
        /// </summary>
        private static int ClearAbandonedFutureEndStates(RecordingTree tree, AbandonedFuturePlan plan)
        {
            var ids = new List<string>(plan.TrimmedIds);
            ids.Sort(StringComparer.Ordinal);
            int cleared = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (plan.PrunedIds.Contains(id)
                    || !tree.Recordings.TryGetValue(id, out Recording rec)
                    || rec == null)
                {
                    continue;
                }

                bool hasTerminal = rec.TerminalStateValue.HasValue;
                bool hasCrewEndStates = rec.CrewEndStates != null || rec.CrewEndStatesResolved;
                double preTrimEndUT = plan.PreTrimEndUT.TryGetValue(id, out double endUT) ? endUT : double.NaN;
                if (!ShouldClearFutureEndState(preTrimEndUT, plan.CutoffUT, hasTerminal, hasCrewEndStates))
                    continue;

                string previousTerminal = hasTerminal ? rec.TerminalStateValue.Value.ToString() : "none";
                int crewEndStateCount = rec.CrewEndStates != null ? rec.CrewEndStates.Count : 0;
                rec.ClearTerminalEndStateForResume(AbandonedFutureReason);
                plan.EndStateClearedIds.Add(id);
                cleared++;
                ParsekLog.Verbose("Scenario",
                    $"Quickload abandoned-future end state cleared: rec={id} previousTerminal={previousTerminal} " +
                    $"preTrimEndUT={preTrimEndUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"cutoffUT={plan.CutoffUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"crewEndStates={crewEndStateCount}");
            }

            return cleared;
        }

        /// <summary>
        /// Purges the plan's tagged game-state events from the abandoned future: for a
        /// surviving trimmed recording, every event strictly after the cutoff (the trim's own
        /// boundary), through <see cref="GameStateStore.PurgeEventsForRecordingAfterUT"/> (live
        /// list, milestones, and the contract snapshots whose accept went with them); for a
        /// pruned recording, which no longer exists, every event it tagged. Untagged events and
        /// other recordings' events are never touched. Returns the number purged.
        /// </summary>
        private static int PurgeAbandonedFutureEvents(AbandonedFuturePlan plan)
        {
            var surviving = new List<string>();
            foreach (string id in plan.TrimmedIds)
            {
                if (!plan.PrunedIds.Contains(id))
                    surviving.Add(id);
            }

            int purged = 0;
            var withFutureEvents = new List<string>(
                GameStateStore.CollectRecordingIdsWithTaggedEventsAfterUT(surviving, plan.CutoffUT));
            withFutureEvents.Sort(StringComparer.Ordinal);
            for (int i = 0; i < withFutureEvents.Count; i++)
            {
                purged += GameStateStore.PurgeEventsForRecordingAfterUT(
                    withFutureEvents[i], plan.CutoffUT, AbandonedFutureReason);
            }

            if (plan.PrunedIds.Count > 0)
            {
                HashSet<string> prunedWithEvents = GameStateStore.CollectRecordingIdsWithTaggedEventsAfterUT(
                    plan.PrunedIds, double.NegativeInfinity);
                if (prunedWithEvents.Count > 0)
                    purged += GameStateStore.PurgeEventsForRecordings(prunedWithEvents, AbandonedFutureReason);
            }

            return purged;
        }

        /// <summary>Why a ledger row belongs to the abandoned future (None: it does not).</summary>
        internal enum AbandonedFutureRowReason
        {
            None = 0,
            AfterCutoff = 1,
            PrunedRecording = 2,
            KerbalAssignment = 3,
        }

        /// <summary>
        /// Pure: whether a ledger row is part of the plan's abandoned future (owner ruling OQ-2:
        /// a quickload into a later-committed flight's quicksave retires that flight's
        /// recording-tagged rows after the quicksave). Only rows tagged to a trimmed recording,
        /// never a route row and never a seed; then: the recording was pruned (it no longer
        /// exists), or the fact happened strictly after the cutoff (the occurrence UT the
        /// commit dedupe uses: a science row is stamped at its recording's end and carries the
        /// capture moment in <c>StartUT</c>), or it is the KerbalAssignment summary of a
        /// recording whose end state was cleared (keyed on recording and kerbal with no UT, so
        /// a stale one would make the re-commit drop the fresh one). Untagged KSC rows are kept.
        /// </summary>
        internal static AbandonedFutureRowReason ClassifyAbandonedFutureRow(
            GameAction action, AbandonedFuturePlan plan)
        {
            if (action == null || plan == null)
                return AbandonedFutureRowReason.None;
            if (string.IsNullOrEmpty(action.RecordingId) || !plan.TrimmedIds.Contains(action.RecordingId))
                return AbandonedFutureRowReason.None;
            if (!string.IsNullOrEmpty(action.RouteId) || RecalculationEngine.IsSeedType(action.Type))
                return AbandonedFutureRowReason.None;
            if (plan.PrunedIds.Contains(action.RecordingId))
                return AbandonedFutureRowReason.PrunedRecording;
            if (LedgerOrchestrator.GetDedupOccurrenceUt(action) > plan.CutoffUT)
                return AbandonedFutureRowReason.AfterCutoff;
            if (action.Type == GameActionType.KerbalAssignment
                && plan.EndStateClearedIds.Contains(action.RecordingId))
            {
                return AbandonedFutureRowReason.KerbalAssignment;
            }
            return AbandonedFutureRowReason.None;
        }

        internal static bool ShouldRetireAbandonedFutureRow(GameAction action, AbandonedFuturePlan plan)
        {
            return ClassifyAbandonedFutureRow(action, plan) != AbandonedFutureRowReason.None;
        }

        private static int RetireAbandonedFutureLedgerRows(
            AbandonedFuturePlan plan, out int afterCutoff, out int kerbalAssignment, out int prunedRecording)
        {
            int after = 0;
            int kerbal = 0;
            int pruned = 0;
            int removed = Ledger.RetireAbandonedFutureActions(action =>
            {
                switch (ClassifyAbandonedFutureRow(action, plan))
                {
                    case AbandonedFutureRowReason.AfterCutoff: after++; return true;
                    case AbandonedFutureRowReason.KerbalAssignment: kerbal++; return true;
                    case AbandonedFutureRowReason.PrunedRecording: pruned++; return true;
                    default: return false;
                }
            }, AbandonedFutureReason);
            afterCutoff = after;
            kerbalAssignment = kerbal;
            prunedRecording = pruned;
            return removed;
        }

        private static HashSet<string> CollectFutureOnlyRecordingIds(RecordingTree tree, double cutoffUT)
        {
            HashSet<string> futureOnlyIds = null;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                string recordingId = kvp.Key;
                Recording rec = kvp.Value;
                if (rec == null
                    || rec.SidecarLoadFailed
                    || string.Equals(recordingId, tree.ActiveRecordingId, StringComparison.Ordinal)
                    || string.Equals(recordingId, tree.RootRecordingId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (rec.StartUT >= cutoffUT)
                {
                    if (futureOnlyIds == null)
                        futureOnlyIds = new HashSet<string>(StringComparer.Ordinal);
                    futureOnlyIds.Add(recordingId);
                }
            }

            return futureOnlyIds;
        }

        private static int PruneFutureOnlyRecordings(RecordingTree tree, HashSet<string> futureOnlyIds)
        {
            if (tree == null || futureOnlyIds == null || futureOnlyIds.Count == 0)
                return 0;

            int removed = 0;
            foreach (string recordingId in futureOnlyIds)
            {
                if (tree.Recordings.Remove(recordingId))
                    removed++;
            }

            if (removed == 0)
                return 0;

            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = tree.BranchPoints[i];
                    bp.ParentRecordingIds?.RemoveAll(id => futureOnlyIds.Contains(id));
                    bp.ChildRecordingIds?.RemoveAll(id => futureOnlyIds.Contains(id));
                }
            }

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec != null && futureOnlyIds.Contains(rec.ParentRecordingId))
                    rec.ParentRecordingId = null;
            }

            return removed;
        }

        private static int RemoveEmptyBranchPoints(RecordingTree tree)
        {
            if (tree == null || tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return 0;

            HashSet<string> removedBranchPointIds = null;
            int removed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                BranchPoint bp = tree.BranchPoints[i];
                int parentCount = bp.ParentRecordingIds != null ? bp.ParentRecordingIds.Count : 0;
                int childCount = bp.ChildRecordingIds != null ? bp.ChildRecordingIds.Count : 0;
                if (parentCount > 0 && childCount > 0)
                    continue;

                if (removedBranchPointIds == null)
                    removedBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
                removedBranchPointIds.Add(bp.Id);
                tree.BranchPoints.RemoveAt(i);
                removed++;
            }

            if (removedBranchPointIds == null || removedBranchPointIds.Count == 0)
                return 0;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;

                if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                    && removedBranchPointIds.Contains(rec.ParentBranchPointId))
                {
                    rec.ParentBranchPointId = null;
                }

                if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                    && removedBranchPointIds.Contains(rec.ChildBranchPointId))
                {
                    rec.ChildBranchPointId = null;
                }
            }

            return removed;
        }

        private static bool TrimOrbitSegmentsPastUT(List<OrbitSegment> segments, double cutoffUT)
        {
            if (segments == null || segments.Count == 0)
                return false;

            bool mutated = false;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                OrbitSegment seg = segments[i];
                if (seg.startUT >= cutoffUT)
                {
                    segments.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (seg.endUT > cutoffUT)
                {
                    seg.endUT = cutoffUT;
                    segments[i] = seg;
                    mutated = true;
                }
            }

            return mutated;
        }

        private static bool TrimTrackSectionsPastUT(List<TrackSection> sections, double cutoffUT)
        {
            if (sections == null || sections.Count == 0)
                return false;

            bool mutated = false;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (section.startUT >= cutoffUT)
                {
                    sections.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                bool sectionMutated = false;
                if (section.endUT > cutoffUT)
                {
                    section.endUT = cutoffUT;
                    sectionMutated = true;
                }

                sectionMutated |= TrimTrackSectionFramesPastUT(ref section, cutoffUT);
                sectionMutated |= TrimTrackSectionCheckpointsPastUT(ref section, cutoffUT);

                bool hasFrames = section.frames != null && section.frames.Count > 0;
                bool hasCheckpoints = section.checkpoints != null && section.checkpoints.Count > 0;
                if (!hasFrames && !hasCheckpoints)
                {
                    sections.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (sectionMutated)
                {
                    RecomputeTrimmedTrackSectionMetadata(ref section);
                    sections[i] = section;
                    mutated = true;
                }
            }

            return mutated;
        }

        private static bool TrimTrackSectionFramesPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.frames == null || section.frames.Count == 0)
                return false;

            int originalCount = section.frames.Count;
            for (int i = section.frames.Count - 1; i >= 0; i--)
            {
                if (section.frames[i].ut > cutoffUT)
                    section.frames.RemoveAt(i);
            }

            if (section.frames.Count == 0)
                section.frames = null;

            int remainingCount = section.frames != null ? section.frames.Count : 0;
            return remainingCount != originalCount;
        }

        private static bool TrimTrackSectionCheckpointsPastUT(ref TrackSection section, double cutoffUT)
        {
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            bool mutated = false;
            for (int i = section.checkpoints.Count - 1; i >= 0; i--)
            {
                OrbitSegment checkpoint = section.checkpoints[i];
                if (checkpoint.startUT >= cutoffUT)
                {
                    section.checkpoints.RemoveAt(i);
                    mutated = true;
                    continue;
                }

                if (checkpoint.endUT > cutoffUT)
                {
                    checkpoint.endUT = cutoffUT;
                    section.checkpoints[i] = checkpoint;
                    mutated = true;
                }
            }

            if (section.checkpoints.Count == 0)
                section.checkpoints = null;

            return mutated;
        }

        private static void RecomputeTrimmedTrackSectionMetadata(ref TrackSection section)
        {
            section.sampleRateHz = 0f;
            section.minAltitude = float.NaN;
            section.maxAltitude = float.NaN;

            if (section.frames == null || section.frames.Count == 0)
                return;

            for (int i = 0; i < section.frames.Count; i++)
            {
                float alt = (float)section.frames[i].altitude;
                if (float.IsNaN(section.minAltitude) || alt < section.minAltitude)
                    section.minAltitude = alt;
                if (float.IsNaN(section.maxAltitude) || alt > section.maxAltitude)
                    section.maxAltitude = alt;
            }

            double duration = section.endUT - section.startUT;
            if (duration > 0.0 && section.frames.Count > 1)
                section.sampleRateHz = (float)(section.frames.Count / duration);
        }

        private static bool RemoveItemsPastUT<T>(List<T> items, double cutoffUT, Func<T, double> getUT)
        {
            if (items == null || items.Count == 0)
                return false;

            int originalCount = items.Count;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (getUT(items[i]) > cutoffUT)
                    items.RemoveAt(i);
            }

            return items.Count != originalCount;
        }
    }
}
