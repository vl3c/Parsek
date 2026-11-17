using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// [ERS-exempt] The own-timeline <see cref="IPlaybackCatalog"/>: answers
    /// from <see cref="RecordingStore"/> and <see cref="PendingTreeSlot"/>.
    /// Anchor and loop resolution and the flight positioner's tail-lift plan
    /// look up an exact recording id, and ERS filtering would hide
    /// NotCommitted active Re-Fly attempts, which are valid focus or anchor
    /// records during the session; so the flat-list fallback and the
    /// committed-list scope read the raw committed list, as the sites they
    /// serve did.
    /// Each variant is a verbatim copy of the lookup it replaced (search
    /// order, match rule, null handling) and logs nothing, because the
    /// lookups run per ghost per frame. Stateless: <see cref="Own"/> is the
    /// one instance.
    /// </summary>
    internal sealed class RecordingPlaybackCatalog : IPlaybackCatalog
    {
        internal static readonly RecordingPlaybackCatalog Own = new RecordingPlaybackCatalog();

        private RecordingPlaybackCatalog()
        {
        }

        public RecordingTree PendingTree =>
            PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null;

        public bool TryFindFocusTree(string recordingId, string treeId, out RecordingTree tree)
        {
            tree = null;
            // Every caller rejects a null or empty focus id before the lookup;
            // the guard keeps the dictionary probe below from throwing.
            if (string.IsNullOrEmpty(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree candidate = committedTrees[i];
                    if (candidate?.Recordings == null)
                        continue;
                    if ((!string.IsNullOrEmpty(treeId)
                            && string.Equals(candidate.Id, treeId, StringComparison.Ordinal))
                        || candidate.Recordings.ContainsKey(recordingId))
                    {
                        tree = candidate;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTree;
            if (pending?.Recordings != null
                && (pending.Recordings.ContainsKey(recordingId)
                    || (!string.IsNullOrEmpty(treeId)
                        && string.Equals(pending.Id, treeId, StringComparison.Ordinal))))
            {
                tree = pending;
                return true;
            }

            return false;
        }

        public bool TryFindTreeHolding(string id, CatalogTreeOrder order, out RecordingTree tree)
        {
            tree = null;
            if (string.IsNullOrEmpty(id))
                return false;

            switch (order)
            {
                case CatalogTreeOrder.CommittedFirstContainsKey:
                    return TryFindTreeCommittedFirstContainsKey(id, out tree);
                default:
                    return false;
            }
        }

        private bool TryFindTreeCommittedFirstContainsKey(string recordingId, out RecordingTree focusTree)
        {
            focusTree = null;
            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.ContainsKey(recordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTree;
            if (pending?.Recordings != null && pending.Recordings.ContainsKey(recordingId))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        public bool TryFindRecording(string id, CatalogRecordingScope scope, out Recording rec)
        {
            rec = null;
            if (string.IsNullOrEmpty(id))
                return false;

            switch (scope)
            {
                case CatalogRecordingScope.TreesThenPendingThenList:
                    return TryFindInTreesThenPendingThenList(id, out rec);
                case CatalogRecordingScope.CommittedListOnly:
                    return TryFindInCommittedList(id, out rec);
                default:
                    return false;
            }
        }

        private static bool TryFindInCommittedList(string recordingId, out Recording recording)
        {
            recording = null;
            IReadOnlyList<Recording> committedRecordings = RecordingStore.CommittedRecordings;
            if (committedRecordings == null)
                return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                Recording candidate = committedRecordings[i];
                if (candidate == null)
                    continue;
                if (string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                {
                    recording = candidate;
                    return true;
                }
            }
            return false;
        }

        private bool TryFindInTreesThenPendingThenList(string recordingId, out Recording recording)
        {
            recording = null;
            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.TryGetValue(recordingId, out recording)
                        && recording != null)
                    {
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTree;
            if (pending?.Recordings != null
                && pending.Recordings.TryGetValue(recordingId, out recording)
                && recording != null)
            {
                return true;
            }

            IReadOnlyList<Recording> committedRecordings = RecordingStore.CommittedRecordings;
            if (committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording candidate = committedRecordings[i];
                    if (candidate != null
                        && string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                    {
                        recording = candidate;
                        return true;
                    }
                }
            }

            recording = null;
            return false;
        }
    }
}
