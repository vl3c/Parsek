using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure payload builder for the <c>RecordingState</c> verb (P5.2). The Unity side
    /// samples <c>ParsekFlight.Instance?.CaptureRecorderState()</c> and feeds the
    /// primitive fields here; with no live flight instance (any non-flight scene, or
    /// flight not yet ready) the recorder is definitionally not recording, so the
    /// snapshot collapses to <c>recording=false</c> with an empty tree and zero points.
    /// Kept pure so the field names / null handling are xUnit-covered without Unity.
    /// </summary>
    internal static class TestCommandRecordingState
    {
        /// <summary>The optional wait argument (S4.4): <c>awaitRecorderLive=refly</c>.</summary>
        internal const string AwaitRecorderLiveKey = "awaitRecorderLive";

        /// <summary>The ONE accepted <c>awaitRecorderLive=</c> wire value. It names the
        /// state waited for - a live recorder WHILE a re-fly session marker is live, the
        /// same conjunction <c>LoadGame allowLiveRecorder=refly</c> admits on - so a future
        /// second state is a second value, never a widening of this one.</summary>
        internal const string AwaitRecorderLiveReFlyValue = "refly";

        /// <summary>Reject reason for an <c>awaitRecorderLive</c> arg that is not the one
        /// accepted wire value.</summary>
        internal const string AwaitRecorderLiveArgInvalidReason = "await-recorder-live-arg-invalid";

        /// <summary>The DEFER reason while <c>awaitRecorderLive=refly</c> waits. A defer,
        /// so a wait that never ends reaches the harness as the dispatcher's TIMEOUT with
        /// this reason as its msg, bounded by the verb's default deferral budget.</summary>
        internal const string ReFlyRecorderNotLiveDeferReason = "refly-recorder-not-live";

        /// <summary>
        /// Parses the optional <c>awaitRecorderLive=</c> arg. FAIL-CLOSED and
        /// case-sensitive, exactly like <c>TestCommandLoadGame.TryParseAllowLiveRecorder</c>:
        /// ABSENT is the plain read (no wait), the literal <c>refly</c> arms the wait, and
        /// anything else - an EMPTY value, <c>true</c>, <c>ReFly</c> - returns false so the
        /// dispatcher refuses it rather than silently reading without waiting.
        /// </summary>
        internal static bool TryParseAwaitRecorderLive(string raw, out bool awaitReFly)
        {
            awaitReFly = false;
            if (raw == null)
                return true;
            if (raw == AwaitRecorderLiveReFlyValue)
            {
                awaitReFly = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// True while an armed <c>awaitRecorderLive=refly</c> read must keep waiting:
        /// the recorder is not live, or no re-fly session marker is. The recorder bit is
        /// the one the <c>LoadGame</c> <c>recording-active</c> guard reads
        /// (<c>ParsekFlight.HasLiveRecorderForTagging</c>), so the step that follows a
        /// satisfied wait sees the same recorder state the wait saw. An unarmed read
        /// never defers.
        /// </summary>
        internal static bool ShouldDeferForReFlyRecorder(
            bool awaitReFly, bool recorderLive, bool reFlyMarkerLive)
        {
            return awaitReFly && !(recorderLive && reFlyMarkerLive);
        }

        /// <summary>
        /// Builds the <c>recording / tree / points / scene</c> payload. When
        /// <paramref name="hasFlight"/> is false the recorder fields are forced to the
        /// not-recording snapshot regardless of the other inputs.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            bool hasFlight, bool isRecording, string treeId, int points, string sceneName)
        {
            bool recording = hasFlight && isRecording;
            string tree = hasFlight ? (treeId ?? string.Empty) : string.Empty;
            int pointCount = hasFlight ? points : 0;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("recording", recording ? "true" : "false"),
                new KeyValuePair<string, string>("tree", tree),
                new KeyValuePair<string, string>("points", pointCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
            };
        }
    }
}
