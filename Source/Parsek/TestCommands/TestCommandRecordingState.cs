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
