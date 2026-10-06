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

        internal bool InSessionRewindPointPartitionPendingForTesting => inSessionRewindPointPartitionPending;

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
                    initialLoadDone, inertGameModePassthroughNode != null, out string skipReason))
            {
                ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                    $"Staged-list handoff capture skipped reason={skipReason} site={reason ?? "<none>"}");
                return;
            }
            InSessionStagedStateHandoff.Capture(this, reason, InSessionStagedStateHandoff.CurrentSaveFolder());
        }

        /// <summary>
        /// Step A of the in-session handoff, in the OnLoad prologue right after the plain-rewind
        /// carries: consumes the handoff, drops it when the load kind owns its lists elsewhere,
        /// otherwise installs memory's supersede rows, rewind retirements and tombstones, the
        /// journal per <see cref="RecordingStore.ShouldCarryMergeJournal"/>, the marker when the
        /// kind decides <c>Memory</c> for it, keeps memory's rewind points for step B, and bumps
        /// both state versions.
        /// </summary>
        internal void ApplyInSessionStagedStateHandoffStepA(EarlyLoadKind early)
        {
            inSessionRewindPointsFromMemory = null;
            inSessionRewindPointPartitionPending = false;

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
        /// Step B of the in-session handoff, after the active-tree restore (and its detach of
        /// the committed copy) and the pending-tree restore: partitions the rewind-point list by
        /// owner (<see cref="InSessionStagedStateHandoff.MergeRewindPointsByOwner"/>) against the
        /// trees memory still holds committed.
        /// </summary>
        internal void ApplyInSessionRewindPointPartitionStepB()
        {
            if (!inSessionRewindPointPartitionPending)
            {
                ParsekLog.Verbose(InSessionStagedStateHandoff.Tag,
                    "In-session RP owner partition: nothing pending (no handoff applied on this load)");
                return;
            }

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
    }
}
