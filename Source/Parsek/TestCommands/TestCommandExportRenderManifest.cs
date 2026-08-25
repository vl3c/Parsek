using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision / payload half of the SINGLE-PHASE <c>ExportRenderManifest</c> verb
    /// (M-A7, design-autotest-render-composition.md). The applier partial
    /// (<c>ParsekTestCommandAddon.ExportRenderManifest.cs</c>) owns the one Unity-coupled
    /// call - <see cref="RenderCompositionRecorder.TryExportNow"/> - and nothing else, so
    /// the response shape a harness spec pins is xUnit-covered without KSP.
    ///
    /// <para>SINGLE-PHASE by construction: the export is a synchronous flush plus one
    /// <c>FileIOUtils.SafeWriteConfigNode</c>, so there is nothing to wait for and no
    /// <c>TryComplete*</c> counterpart (the MissionConfig / SimulateStockSwitchClick
    /// shape). A deferred verb here would invent a wait that does not exist.</para>
    /// </summary>
    internal static class TestCommandExportRenderManifest
    {
        /// <summary>
        /// The manifest file the recorder writes into the KSP root, echoed in the OK payload
        /// so a spec never hardcodes the name. Referenced from the recorder rather than
        /// re-spelled: two copies of a file-name literal is exactly the drift this seam's
        /// other pure builders avoid.
        /// </summary>
        internal const string ManifestFileName = RenderCompositionRecorder.ManifestFileName;

        /// <summary>
        /// Reject reason when the recorder was never armed. NOT a defer: the arm gate is the
        /// <c>PARSEK_RENDER_MANIFEST</c> env var read ONCE at addon Awake (or an in-game
        /// cell's force flag), so waiting could never turn this into a success and would
        /// only burn the dispatch budget.
        /// </summary>
        internal const string NotArmedReason = RenderCompositionRecorder.ErrorNotArmed;

        /// <summary>
        /// Fallback error reason for a <c>TryExportNow</c> failure that reported no reason of
        /// its own (the recorder always sets one; this keeps the response typed if it ever
        /// does not).
        /// </summary>
        internal const string ExportFailedReason = "manifest-export-failed";

        /// <summary>The OK payload: the written path plus the four headline record counts.</summary>
        internal static List<KeyValuePair<string, string>> BuildResultPayload(
            string path, int dwells, int transitions, int planUnits, int clockEvents)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("path", path ?? string.Empty),
                new KeyValuePair<string, string>("manifest", ManifestFileName),
                new KeyValuePair<string, string>(
                    "planUnits", planUnits.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "dwells", dwells.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "transitions", transitions.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    "clockEvents", clockEvents.ToString(CultureInfo.InvariantCulture)),
            };

        /// <summary>
        /// Normalizes the recorder's error string into the response reason: an empty / null
        /// error becomes <see cref="ExportFailedReason"/> so the terminal is never blank.
        /// </summary>
        internal static string ErrorReason(string recorderError)
            => string.IsNullOrEmpty(recorderError) ? ExportFailedReason : recorderError;
    }
}
