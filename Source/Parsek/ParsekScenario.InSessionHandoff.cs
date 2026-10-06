using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekScenario
    {
        // The rewind-point half of the in-session handoff, held from step A (OnLoad prologue)
        // to step B (after the active and pending tree restores) of the SAME OnLoad. Instance
        // state, so it dies with the instance if the load throws in between.
        private List<RewindPoint> inSessionRewindPointsFromMemory;
        private bool inSessionRewindPointPartitionPending;

        // The loaded save's own supersede rows, retirements and tombstones, kept by step A when it
        // installs memory's, for step B's resumed-tree rule. Same lifetime as the field above.
        private List<RecordingSupersedeRelation> inSessionLoadedSupersedes;
        private List<RecordingRewindRetirement> inSessionLoadedRetirements;
        private List<LedgerTombstone> inSessionLoadedTombstones;
        private bool inSessionResumedTreeRowsPending;

        // True once this instance's OnLoad got through the staging load and step A: only then are
        // its staged lists a completed load's, fit to hand to the next load.
        private bool inSessionStagedStateLoaded;

        internal bool InSessionRewindPointPartitionPendingForTesting => inSessionRewindPointPartitionPending;
        internal bool InSessionResumedTreeRowsPendingForTesting => inSessionResumedTreeRowsPending;
        internal bool InSessionStagedStateLoadedForTesting => inSessionStagedStateLoaded;

        /// <summary>Test seam: a scenario built in a test stands for an instance whose OnLoad completed.</summary>
        internal void MarkInSessionStagedStateLoadedForTesting() => inSessionStagedStateLoaded = true;

        /// <summary>
        /// What a committed-copy restore detached on this load: the resumed tree's id and every
        /// recording id of the detached committed copy and of the resumed tree.
        /// </summary>
        internal sealed class QuickloadResumeDetach
        {
            internal string TreeId;
            internal HashSet<string> RecordingIds;
        }

        // Set by TryRestoreActiveTreeNode (cleared at its start), taken by step B of the same OnLoad.
        private static QuickloadResumeDetach lastQuickloadResumeDetach;

        internal static void ClearQuickloadResumeDetach() => lastQuickloadResumeDetach = null;

        internal static QuickloadResumeDetach PeekQuickloadResumeDetachForTesting() => lastQuickloadResumeDetach;

        private static List<string> CollectCommittedCopyRecordingIds(RecordingTree loadedTree)
        {
            var ids = new List<string>();
            RecordingTree committedCopy = FindCommittedTreeById(loadedTree.Id, exclude: loadedTree);
            if (committedCopy?.Recordings != null)
                ids.AddRange(committedCopy.Recordings.Keys);
            return ids;
        }

        /// <summary>
        /// Called by <see cref="TryRestoreActiveTreeNode"/> after a
        /// <see cref="CommittedCopyRestoreAction.ResumeFromQuicksave"/> restore detached the
        /// committed copy and stashed the save's tree: the flight resumes from the save and the
        /// commit's future is retired, so step B hands the rows naming this tree back to the save.
        /// </summary>
        private static void NoteQuickloadResumeDetach(RecordingTree resumed, List<string> committedCopyIds)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (committedCopyIds != null)
            {
                for (int i = 0; i < committedCopyIds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(committedCopyIds[i]))
                        ids.Add(committedCopyIds[i]);
                }
            }
            if (resumed?.Recordings != null)
            {
                foreach (string id in resumed.Recordings.Keys)
                {
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
            lastQuickloadResumeDetach = new QuickloadResumeDetach { TreeId = resumed?.Id, RecordingIds = ids };
            ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                $"Quickload resume detach noted: tree={resumed?.Id ?? "<none>"} "
                + $"recordings={ids.Count.ToString(CultureInfo.InvariantCulture)} "
                + $"(committed copy {(committedCopyIds?.Count ?? 0).ToString(CultureInfo.InvariantCulture)}); "
                + "step B hands the rows naming it to the save");
        }

        internal const string DiscardReFlyLoadMarkerClearReason = "discard-refly-load";

        /// <summary>
        /// Captures this instance's staged lists, journal and marker into
        /// <see cref="InSessionStagedStateHandoff"/> for the next OnLoad. Called from
        /// <see cref="OnDestroy"/> while this instance is still <see cref="Instance"/>, and from a
        /// successor's <see cref="OnAwake"/> when this instance is still registered then.
        /// </summary>
        internal void CaptureInSessionStagedStateHandoff(string reason)
        {
            if (!InSessionStagedStateHandoff.ShouldCapture(
                    initialLoadDone, inertGameModePassthroughNode != null, inSessionStagedStateLoaded,
                    out string skipReason))
            {
                if (skipReason == InSessionStagedStateHandoff.CaptureSkipStagedStateNotLoaded)
                {
                    ParsekLog.Warn(InSessionStagedStateHandoff.Tag,
                        $"Staged-list handoff capture skipped reason={skipReason} site={reason ?? "<none>"}: "
                        + "this instance's OnLoad did not get through step A, so the next load keeps its save's lists");
                }
                else
                {
                    ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                        $"Staged-list handoff capture skipped reason={skipReason} site={reason ?? "<none>"}");
                }
                return;
            }
            InSessionStagedStateHandoff.Capture(this, reason, InSessionStagedStateHandoff.CurrentSaveFolder());
        }

        /// <summary>
        /// Step A of the in-session handoff, in the OnLoad prologue right after the plain-rewind
        /// carries: consumes the handoff, drops it when the load kind owns its lists elsewhere,
        /// otherwise installs memory's supersede rows, rewind retirements and tombstones (keeping
        /// the save's own copies for step B), the journal per
        /// <see cref="RecordingStore.ShouldCarryMergeJournal"/>, the marker when the kind decides
        /// <c>Memory</c> for it, keeps memory's rewind points for step B, and bumps both state
        /// versions. Marks this instance's staged state loaded once it returns normally, on every
        /// path: the instance may then hand its lists to the next load.
        /// </summary>
        internal void ApplyInSessionStagedStateHandoffStepA(EarlyLoadKind early)
        {
            ApplyInSessionStagedStateHandoffStepACore(early);
            inSessionStagedStateLoaded = true;
        }

        private void ApplyInSessionStagedStateHandoffStepACore(EarlyLoadKind early)
        {
            inSessionRewindPointsFromMemory = null;
            inSessionRewindPointPartitionPending = false;
            inSessionLoadedSupersedes = null;
            inSessionLoadedRetirements = null;
            inSessionLoadedTombstones = null;
            inSessionResumedTreeRowsPending = false;

            var handoff = InSessionStagedStateHandoff.Take();
            if (handoff == null)
            {
                string folder = InSessionStagedStateHandoff.CurrentSaveFolder();
                string owner = InSessionStagedStateHandoff.DecideStepA(early, folder, folder);
                if (owner == null)
                {
                    ParsekLog.Warn(InSessionStagedStateHandoff.Tag,
                        $"In-session load found no staged-list handoff (kind={early}): the supersede rows, "
                        + "retirements, tombstones, journal and rewind points stay as the loaded save carries them "
                        + "(the previous scenario instance was not torn down before this load)");
                }
                else
                {
                    ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                        $"No staged-list handoff to consume (kind={early}, owner={owner})");
                }
                return;
            }

            string dropReason = InSessionStagedStateHandoff.DecideStepA(
                early, handoff.SaveFolder, InSessionStagedStateHandoff.CurrentSaveFolder());
            if (dropReason != null)
            {
                InSessionStagedStateHandoff.LogDropped(handoff, dropReason);
                return;
            }

            int loadedSupersedes = RecordingSupersedes?.Count ?? 0;
            int loadedRetirements = RecordingRewindRetirements?.Count ?? 0;
            int loadedTombstones = LedgerTombstones?.Count ?? 0;
            inSessionLoadedSupersedes = RecordingSupersedes != null
                ? new List<RecordingSupersedeRelation>(RecordingSupersedes)
                : new List<RecordingSupersedeRelation>();
            inSessionLoadedRetirements = RecordingRewindRetirements != null
                ? new List<RecordingRewindRetirement>(RecordingRewindRetirements)
                : new List<RecordingRewindRetirement>();
            inSessionLoadedTombstones = LedgerTombstones != null
                ? new List<LedgerTombstone>(LedgerTombstones)
                : new List<LedgerTombstone>();
            inSessionResumedTreeRowsPending = true;

            RecordingSupersedes = RecordingStore.MergeCarriedStagedList(
                handoff.Supersedes, RecordingSupersedes, r => r.RelationId,
                out int supRestored, out int supStale);
            RecordingRewindRetirements = RecordingStore.MergeCarriedStagedList(
                handoff.Retirements, RecordingRewindRetirements, r => r.RetirementId,
                out int retRestored, out int retStale);
            LedgerTombstones = RecordingStore.MergeCarriedStagedList(
                handoff.Tombstones, LedgerTombstones, t => t.TombstoneId,
                out int tombRestored, out int tombStale);

            MergeJournal loadedJournal = ActiveMergeJournal;
            bool journalFromMemory = false;
            if (LoadReconcilePolicy.DecideEarly(early, LoadStateCategory.MergeJournal).Action
                == LoadReconcileAction.Memory)
            {
                journalFromMemory = RecordingStore.ShouldCarryMergeJournal(handoff.Journal);
                if (journalFromMemory)
                    ActiveMergeJournal = handoff.Journal;
                else
                    ParsekLog.Warn(InSessionStagedStateHandoff.Tag,
                        $"In-session handoff kept the loaded merge journal "
                        + $"{InSessionStagedStateHandoff.DescribeJournal(loadedJournal)}: memory's journal "
                        + $"{InSessionStagedStateHandoff.DescribeJournal(handoff.Journal)} was still in flight "
                        + "when the previous scene was torn down");
            }

            string markerSource = ApplyHandoffMarker(early, handoff);

            if (LoadReconcilePolicy.DecideEarly(early, LoadStateCategory.RewindPoints).Action
                == LoadReconcileAction.OwnerPartition)
            {
                inSessionRewindPointsFromMemory = handoff.RewindPoints ?? new List<RewindPoint>();
                inSessionRewindPointPartitionPending = true;
            }

            BumpSupersedeStateVersion();
            BumpTombstoneStateVersion();

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(InSessionStagedStateHandoff.Tag,
                $"In-session staged lists from memory: kind={early} captured={handoff.CaptureReason ?? "<none>"}; " +
                $"supersedes installed={RecordingSupersedes.Count.ToString(ic)} " +
                $"loadedFromSave={loadedSupersedes.ToString(ic)} restored={supRestored.ToString(ic)} " +
                $"staleDropped={supStale.ToString(ic)}; " +
                $"retirements installed={RecordingRewindRetirements.Count.ToString(ic)} " +
                $"loadedFromSave={loadedRetirements.ToString(ic)} restored={retRestored.ToString(ic)} " +
                $"staleDropped={retStale.ToString(ic)}; " +
                $"tombstones installed={LedgerTombstones.Count.ToString(ic)} " +
                $"loadedFromSave={loadedTombstones.ToString(ic)} restored={tombRestored.ToString(ic)} " +
                $"staleDropped={tombStale.ToString(ic)}; " +
                $"journal installed={InSessionStagedStateHandoff.DescribeJournal(ActiveMergeJournal)} " +
                $"loadedFromSave={InSessionStagedStateHandoff.DescribeJournal(loadedJournal)} " +
                $"source={(journalFromMemory ? "memory" : "save")}; " +
                $"marker source={markerSource}; " +
                $"rewindPoints memory={(inSessionRewindPointsFromMemory?.Count ?? 0).ToString(ic)} " +
                $"partition={(inSessionRewindPointPartitionPending ? "pending" : "none")}");
        }

        /// <summary>
        /// The marker half of step A: on a kind that decides <c>Memory</c> for the marker (the
        /// Discard Re-fly load) memory's marker replaces the loaded one. Memory's marker is null
        /// there (the handler cleared it), so the loaded one is cleared through
        /// <see cref="ClearActiveReFlySessionMarker"/>, which also drops the render-session anchors
        /// and the prune hand-over note the staging load rebuilt from it. Returns the log token.
        /// </summary>
        private string ApplyHandoffMarker(EarlyLoadKind early, InSessionStagedStateHandoff.Snapshot handoff)
        {
            if (LoadReconcilePolicy.DecideEarly(early, LoadStateCategory.ReFlyMarker).Action
                != LoadReconcileAction.Memory)
            {
                return "save";
            }

            if (handoff.Marker != null)
            {
                ParsekLog.Warn(InSessionStagedStateHandoff.Tag,
                    $"In-session handoff carries a live Re-Fly marker sess={handoff.Marker.SessionId ?? "<no-id>"} "
                    + $"on a {early} load (the discard clears it first); the loaded marker "
                    + $"{(ActiveReFlySessionMarker != null ? ActiveReFlySessionMarker.SessionId ?? "<no-id>" : "none")} "
                    + "stands for LoadTimeSweep to validate");
                return "save(unexpected-memory-marker)";
            }

            string loadedSession = ActiveReFlySessionMarker != null
                ? ActiveReFlySessionMarker.SessionId ?? "<no-id>"
                : "none";
            ClearActiveReFlySessionMarker(DiscardReFlyLoadMarkerClearReason);
            ReFlyRevertButtonGate.Apply("OnLoad:" + DiscardReFlyLoadMarkerClearReason);
            return "memory(none; loaded=" + loadedSession + ")";
        }

        /// <summary>
        /// Step B of the in-session handoff, after the active-tree restore (and its detach of the
        /// committed copy) and the pending-tree restore: partitions the rewind-point list by owner
        /// (<see cref="InSessionStagedStateHandoff.MergeRewindPointsByOwner"/>) against the trees
        /// memory still holds committed, then gives the rows of a tree this load resumed from the
        /// save back to the save (<see cref="ApplyInSessionResumedTreeRowRule"/>). Consumes the
        /// restore's resume note on every load that reaches it.
        /// </summary>
        internal void ApplyInSessionStagedStateHandoffStepB()
        {
            QuickloadResumeDetach detach = lastQuickloadResumeDetach;
            lastQuickloadResumeDetach = null;

            if (!inSessionRewindPointPartitionPending && !inSessionResumedTreeRowsPending)
            {
                ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                    "In-session handoff step B: nothing pending (no handoff applied on this load)"
                    + (detach != null
                        ? $"; resumed tree={detach.TreeId ?? "<none>"} keeps the save's rows and rewind points"
                        : ""));
                ClearInSessionLoadedRowCopies();
                return;
            }

            if (inSessionRewindPointPartitionPending)
                ApplyInSessionRewindPointPartition();
            if (inSessionResumedTreeRowsPending)
                ApplyInSessionResumedTreeRowRule(detach);
        }

        private void ClearInSessionLoadedRowCopies()
        {
            inSessionLoadedSupersedes = null;
            inSessionLoadedRetirements = null;
            inSessionLoadedTombstones = null;
            inSessionResumedTreeRowsPending = false;
        }

        private void ApplyInSessionRewindPointPartition()
        {
            var memory = inSessionRewindPointsFromMemory ?? new List<RewindPoint>();
            inSessionRewindPointsFromMemory = null;
            inSessionRewindPointPartitionPending = false;

            InSessionStagedStateHandoff.CollectCommittedTreeRewindLinks(
                RecordingStore.CommittedTrees, out HashSet<string> committedBranchPointIds,
                out HashSet<string> committedRewindPointIds);
            Func<RewindPoint, bool> ownedByCommittedTree = rp =>
                (!string.IsNullOrEmpty(rp.BranchPointId) && committedBranchPointIds.Contains(rp.BranchPointId))
                || (!string.IsNullOrEmpty(rp.RewindPointId) && committedRewindPointIds.Contains(rp.RewindPointId));

            int loadedCount = RewindPoints?.Count ?? 0;
            var decisions = new List<string>();
            RewindPoints = InSessionStagedStateHandoff.MergeRewindPointsByOwner(
                memory, RewindPoints, ownedByCommittedTree, out RewindPointPartitionCounts counts, decisions);
            RecordingsTableUI.ClearAllRewindSlotCanInvokeLogState();

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(InSessionStagedStateHandoff.Tag,
                $"In-session RP owner partition: installed={RewindPoints.Count.ToString(ic)} " +
                $"memory={memory.Count.ToString(ic)} loadedFromSave={loadedCount.ToString(ic)} " +
                InSessionStagedStateHandoff.FormatPartitionCounts(counts) +
                $" committedTrees={RecordingStore.CommittedTrees.Count.ToString(ic)}");
            if (decisions.Count > 0)
            {
                ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                    "In-session RP owner partition decisions: "
                    + InSessionStagedStateHandoff.JoinBounded(decisions, 20));
            }
        }

        /// <summary>
        /// The resumed-tree row rule: a supersede row, rewind retirement or tombstone that names a
        /// recording of the tree a <see cref="CommittedCopyRestoreAction.ResumeFromQuicksave"/>
        /// restore detached on this load follows the loaded save
        /// (<see cref="InSessionStagedStateHandoff.ApplyResumedTreeRowRule{T}"/>): the flight
        /// resumes from the save and its commit's future is retired, and a Re-Fly merged into it
        /// after the save is part of that future. Exception (owner ruling OQ-1): when the loaded
        /// marker resumes the Re-Fly attempt that wrote a row, memory's copy stays, so a later
        /// discard of the resumed session prunes it. Any other detach (a load outside flight, a
        /// revert) keeps the committed future, so its rows stay from memory.
        /// </summary>
        private void ApplyInSessionResumedTreeRowRule(QuickloadResumeDetach detach)
        {
            var loadedSupersedes = inSessionLoadedSupersedes;
            var loadedRetirements = inSessionLoadedRetirements;
            var loadedTombstones = inSessionLoadedTombstones;
            ClearInSessionLoadedRowCopies();

            if (detach == null || detach.RecordingIds == null || detach.RecordingIds.Count == 0)
            {
                ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                    "In-session resumed-tree rows: no tree detached and resumed on this load; "
                    + "the supersede rows, retirements and tombstones stay from memory");
                return;
            }

            HashSet<string> treeRecordingIds = detach.RecordingIds;
            HashSet<string> attemptIds = ResolveResumedReFlyAttemptIds(detach, out string attemptToken);

            var tombstoneActionIds = new HashSet<string>(StringComparer.Ordinal);
            AddTombstoneActionIds(LedgerTombstones, tombstoneActionIds);
            AddTombstoneActionIds(loadedTombstones, tombstoneActionIds);
            Dictionary<string, string> actionRecordingIds = Ledger.CollectRecordingIdsForActions(tombstoneActionIds);

            var decisions = new List<string>();
            RecordingSupersedes = InSessionStagedStateHandoff.ApplyResumedTreeRowRule(
                RecordingSupersedes, loadedSupersedes, r => r.RelationId,
                r => InSessionStagedStateHandoff.SupersedeNamesAny(r, treeRecordingIds),
                r => WrittenBy(InSessionStagedStateHandoff.SupersedeWriterId(r), attemptIds),
                out ResumedTreeRowCounts supersedeCounts, decisions);
            RecordingRewindRetirements = InSessionStagedStateHandoff.ApplyResumedTreeRowRule(
                RecordingRewindRetirements, loadedRetirements, r => r.RetirementId,
                r => InSessionStagedStateHandoff.RetirementNamesAny(r, treeRecordingIds),
                r => WrittenBy(InSessionStagedStateHandoff.RetirementWriterId(r), attemptIds),
                out ResumedTreeRowCounts retirementCounts, decisions);
            LedgerTombstones = InSessionStagedStateHandoff.ApplyResumedTreeRowRule(
                LedgerTombstones, loadedTombstones, t => t.TombstoneId,
                t => InSessionStagedStateHandoff.TombstoneNamesAny(t, treeRecordingIds, actionRecordingIds),
                t => WrittenBy(InSessionStagedStateHandoff.TombstoneWriterId(t), attemptIds),
                out ResumedTreeRowCounts tombstoneCounts, decisions);

            BumpSupersedeStateVersion();
            BumpTombstoneStateVersion();

            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(InSessionStagedStateHandoff.Tag,
                $"In-session resumed-tree rows: tree={detach.TreeId ?? "<none>"} " +
                $"recordings={treeRecordingIds.Count.ToString(ic)} attempt={attemptToken}; " +
                InSessionStagedStateHandoff.FormatResumedTreeRowCounts("supersedes", supersedeCounts) + "; " +
                InSessionStagedStateHandoff.FormatResumedTreeRowCounts("retirements", retirementCounts) + "; " +
                InSessionStagedStateHandoff.FormatResumedTreeRowCounts("tombstones", tombstoneCounts));
            if (decisions.Count > 0)
            {
                ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                    "In-session resumed-tree row decisions: "
                    + InSessionStagedStateHandoff.JoinBounded(decisions, 20));
            }
        }

        /// <summary>
        /// The attempt recordings whose rows the OQ-1 exception keeps: the loaded marker's attempt
        /// in the resumed tree (<see cref="MergeDialog.CollectReFlyAttemptOwnedRecordingIds"/>, the
        /// set the discard prunes) when the marker resumes it, else empty.
        /// </summary>
        private HashSet<string> ResolveResumedReFlyAttemptIds(QuickloadResumeDetach detach, out string token)
        {
            var marker = ActiveReFlySessionMarker;
            RecordingTree resumed = RecordingStore.HasPendingTree
                && RecordingStore.PendingTree != null
                && string.Equals(RecordingStore.PendingTree.Id, detach.TreeId, StringComparison.Ordinal)
                    ? RecordingStore.PendingTree
                    : null;
            ICollection<string> resumedIds = resumed?.Recordings?.Keys;
            if (!InSessionStagedStateHandoff.LoadedMarkerResumesAttempt(marker, detach.TreeId, resumedIds, out string reason))
            {
                token = marker == null
                    ? "none(" + reason + ")"
                    : "none(" + reason + " sess=" + (marker.SessionId ?? "<no-id>") + ")";
                return new HashSet<string>(StringComparer.Ordinal);
            }
            HashSet<string> ids = MergeDialog.CollectReFlyAttemptOwnedRecordingIds(resumed, marker)
                ?? new HashSet<string>(StringComparer.Ordinal);
            token = "sess=" + (marker.SessionId ?? "<no-id>") + " ids="
                + ids.Count.ToString(CultureInfo.InvariantCulture) + " (" + reason + ")";
            return ids;
        }

        private static bool WrittenBy(string writerRecordingId, HashSet<string> attemptIds)
            => !string.IsNullOrEmpty(writerRecordingId) && attemptIds != null && attemptIds.Contains(writerRecordingId);

        private static void AddTombstoneActionIds(List<LedgerTombstone> tombstones, HashSet<string> into)
        {
            if (tombstones == null)
                return;
            for (int i = 0; i < tombstones.Count; i++)
            {
                if (tombstones[i] != null && !string.IsNullOrEmpty(tombstones[i].ActionId))
                    into.Add(tombstones[i].ActionId);
            }
        }
    }
}
