using System;

namespace Parsek
{
    /// <summary>
    /// The ledger's timeline-changed signal. A leaf type, so the code that raises it outside
    /// the ledger (<see cref="RecordingTreeSplitter"/>) and the windows that subscribe to it
    /// name it without referencing <see cref="LedgerOrchestrator"/>.
    /// </summary>
    internal static class LedgerTimelineSignal
    {
        /// <summary>
        /// Fired after RecalculateAndPatch completes - signals that timeline data
        /// (recordings, ledger actions, milestone replay cursors, or event
        /// visibility) may have changed. Subscribed by ParsekUI to invalidate the
        /// TimelineWindowUI cache and by StockUiOverlayController to refresh open
        /// stock-screen badges.
        ///
        /// Future code that mutates MilestoneStore.LastReplayedEventIndex or
        /// GameStateStore event visibility must end with RecalculateAndPatch()
        /// or invoke this signal directly, otherwise open stock overlays can stay
        /// stale until their building is reopened.
        /// </summary>
        internal static Action OnTimelineDataChanged;
    }
}
