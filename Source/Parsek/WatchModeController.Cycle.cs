using System;
using System.Collections.Generic;

namespace Parsek
{
    internal partial class WatchModeController
    {
        /// <summary>
        /// Resolution of a single W-key cycle press in watch mode. Pure data; populated
        /// by <see cref="ResolveCycleTarget"/> and consumed by <see cref="CycleToNextWatchable"/>.
        /// </summary>
        internal readonly struct CycleResolution
        {
            public readonly int NextIndex;
            public readonly string NextRecordingId;
            public readonly bool HasTarget;        // true when caller should EnterWatchMode(NextIndex)
            public readonly bool IsToggleOff;      // single-entry rotation that IS already watched
            public readonly int TotalEligible;
            public readonly int Position;
            public readonly bool IsWrap;

            public CycleResolution(
                int nextIndex,
                string nextRecordingId,
                bool hasTarget,
                bool isToggleOff,
                int totalEligible,
                int position,
                bool isWrap)
            {
                NextIndex = nextIndex;
                NextRecordingId = nextRecordingId;
                HasTarget = hasTarget;
                IsToggleOff = isToggleOff;
                TotalEligible = totalEligible;
                Position = position;
                IsWrap = isWrap;
            }

            public static CycleResolution Empty =>
                new CycleResolution(-1, null, false, false, 0, 0, false);
        }

        /// <summary>
        /// Pure resolver for the W-key watch-rotation cycle. Builds a descendants
        /// set covering every committed index, resolves the currently-watched
        /// RecordingId from <paramref name="currentWatchedIndex"/>, and delegates
        /// to <see cref="GhostPlaybackLogic.AdvanceGroupWatchCursor"/> for the
        /// stable-order rotation walk.
        ///
        /// HasTarget is true when the caller should call EnterWatchMode(NextIndex);
        /// false when the rotation is empty or has reduced to a single entry that
        /// is already being watched (toggle-off — the caller should NOT exit watch,
        /// just no-op so the player keeps observing).
        /// </summary>
        internal static CycleResolution ResolveCycleTarget(
            IReadOnlyList<Recording> committed,
            Func<int, bool> isEligible,
            int currentWatchedIndex,
            string cursorRecordingId)
        {
            if (committed == null || committed.Count == 0 || isEligible == null)
                return CycleResolution.Empty;

            var descendants = new HashSet<int>();
            for (int i = 0; i < committed.Count; i++)
                descendants.Add(i);

            string watchedId = currentWatchedIndex >= 0 && currentWatchedIndex < committed.Count
                ? committed[currentWatchedIndex]?.RecordingId
                : null;

            var rotation = GhostPlaybackLogic.AdvanceGroupWatchCursor(
                descendants, committed, isEligible, cursorRecordingId, watchedId);

            if (rotation.NextRecordingId == null)
                return CycleResolution.Empty;

            int nextIdx = GhostPlaybackLogic.FindRecordingIndexById(committed, rotation.NextRecordingId);
            bool hasTarget = !rotation.IsToggleOff
                && nextIdx >= 0
                && nextIdx != currentWatchedIndex;

            return new CycleResolution(
                nextIdx,
                rotation.NextRecordingId,
                hasTarget,
                rotation.IsToggleOff,
                rotation.TotalEligible,
                rotation.Position,
                rotation.IsWrap);
        }

        /// <summary>
        /// Pure static helper: computes the new watch index after a recording is deleted.
        /// Returns (newIndex, newId) where newIndex=-1 means watch mode should exit.
        /// </summary>
        internal static (int newIndex, string newId) ComputeWatchIndexAfterDelete(
            int watchedIndex, string watchedId, int deletedIndex,
            IReadOnlyList<Recording> recordings)
        {
            if (deletedIndex == watchedIndex)
                return (-1, null);

            int newIndex = watchedIndex;
            if (deletedIndex < watchedIndex)
                newIndex = watchedIndex - 1;

            // Verify by ID -- the recording at the new index should match
            if (newIndex >= 0 && newIndex < recordings.Count &&
                recordings[newIndex].RecordingId == watchedId)
            {
                return (newIndex, watchedId);
            }

            // ID mismatch -- scan for correct index
            for (int j = 0; j < recordings.Count; j++)
            {
                if (recordings[j].RecordingId == watchedId)
                    return (j, watchedId);
            }

            // Not found -- exit watch mode
            return (-1, null);
        }
    }
}
