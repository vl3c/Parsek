namespace Parsek
{
    /// <summary>
    /// Search scope of <see cref="IPlaybackCatalog.TryFindRecording"/>. Each
    /// member names the exact order of one family of lookup sites, so a call
    /// site states its own order instead of inheriting a shared one.
    /// </summary>
    internal enum CatalogRecordingScope
    {
        /// <summary>
        /// Committed trees in list order (a non-null value under the id), then
        /// the pending tree (same match), then the flat committed recording
        /// list by ordinal id. The recorded-anchor pose resolver's order.
        /// </summary>
        TreesThenPendingThenList,

        /// <summary>
        /// The flat committed recording list only, in list order, skipping
        /// null entries, by ordinal id. The flight positioner's diagnostic and
        /// tail-lift order (the order of
        /// <see cref="RecordingStore.TryFindCommittedRecordingById"/>).
        /// </summary>
        CommittedListOnly,
    }

    /// <summary>
    /// Search order of <see cref="IPlaybackCatalog.TryFindTreeHolding"/>. Each
    /// member names the exact order and match rule of one lookup site.
    /// </summary>
    internal enum CatalogTreeOrder
    {
        /// <summary>
        /// Committed trees in list order, then the pending tree; a tree
        /// matches when its recordings dictionary contains the id (a null value
        /// under the id still matches; a null dictionary never does). The
        /// flight positioner's relative-anchor focus-tree order.
        /// </summary>
        CommittedFirstContainsKey,
    }

    /// <summary>
    /// The recordings one playback lane may resolve by id: the trees, the
    /// pending tree and the flat committed list of one timeline. Playback code
    /// that looks a recording or a tree up by id asks this port instead of
    /// reading <see cref="RecordingStore"/> / <see cref="PendingTreeSlot"/>
    /// directly, so a second lane can answer from its own data. Each variant
    /// keeps the search order and match rule of the sites it serves; the
    /// variants are not interchangeable.
    /// </summary>
    internal interface IPlaybackCatalog
    {
        /// <summary>The pending tree, or null when the slot is empty.</summary>
        RecordingTree PendingTree { get; }

        /// <summary>
        /// The tree a recording plays in: committed trees in list order first,
        /// then the pending tree. A tree matches when its id equals
        /// <paramref name="treeId"/> (non-empty) or its recordings contain
        /// <paramref name="recordingId"/>; a tree with a null recordings
        /// dictionary never matches. False for a null or empty recording id.
        /// </summary>
        bool TryFindFocusTree(string recordingId, string treeId, out RecordingTree tree);

        /// <summary>
        /// The tree holding recording <paramref name="id"/>, searched in the
        /// order <paramref name="order"/> names. False, with a null tree, for a
        /// null or empty id.
        /// </summary>
        bool TryFindTreeHolding(string id, CatalogTreeOrder order, out RecordingTree tree);

        /// <summary>
        /// A recording by exact id in the order <paramref name="scope"/> names.
        /// False, with a null recording, for a null or empty id.
        /// </summary>
        bool TryFindRecording(string id, CatalogRecordingScope scope, out Recording rec);
    }
}
