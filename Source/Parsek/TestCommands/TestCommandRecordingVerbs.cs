using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure success-payload builders for the four recorder/tree verbs (P5.4 / P5.5).
    /// The Unity side samples ParsekFlight state around the actual
    /// <c>StartRecording</c> / <c>StopRecording</c> / <c>CommitTreeFlight</c> /
    /// <c>AutoDiscardActiveTreeWithMessage</c> calls (which all return void) and feeds
    /// the sampled booleans here. The no-op flags (<c>already</c> / <c>idle</c> /
    /// <c>nothing</c>) are the idempotency signals the orchestrator reads, so their
    /// presence rules are pinned by xUnit without Unity.
    /// </summary>
    internal static class TestCommandRecordingVerbs
    {
        /// <summary>
        /// StartRecording payload: always <c>recordingId</c> (sampled after the call);
        /// <c>already=true</c> only when a recorder was already live (no second recorder
        /// was forced).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStartPayload(bool alreadyLive, string recordingId)
        {
            var p = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("recordingId", recordingId ?? string.Empty),
            };
            if (alreadyLive)
                p.Add(new KeyValuePair<string, string>("already", "true"));
            return p;
        }

        /// <summary>
        /// StopRecording payload: <c>stopped</c> reflects whether a live recorder was
        /// stopped; <c>idle=true</c> only when there was no recorder (idempotent no-op).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStopPayload(bool wasLive)
        {
            var p = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("stopped", wasLive ? "true" : "false"),
            };
            if (!wasLive)
                p.Add(new KeyValuePair<string, string>("idle", "true"));
            return p;
        }

        /// <summary>CommitTree success payload (only reached with an active tree).</summary>
        internal static List<KeyValuePair<string, string>> BuildCommitPayload()
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("committed", "true"),
            };

        /// <summary>
        /// DiscardTree payload: <c>discarded=true</c> when an active tree was torn down;
        /// <c>nothing=true</c> when there was no active tree (idempotent no-op).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildDiscardPayload(bool hadTree)
            => new List<KeyValuePair<string, string>>
            {
                hadTree
                    ? new KeyValuePair<string, string>("discarded", "true")
                    : new KeyValuePair<string, string>("nothing", "true"),
            };
    }
}
