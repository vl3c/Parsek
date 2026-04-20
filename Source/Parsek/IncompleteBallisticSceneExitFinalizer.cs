using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Thin scene-exit seam for incomplete-ballistic finalization. The concrete
    /// patched-snapshot and extrapolation implementation can plug in later; the
    /// default behavior is a no-op so existing finalization remains unchanged.
    /// </summary>
    internal struct IncompleteBallisticFinalizationResult
    {
        public TerminalState terminalState;
        public double terminalUT;
        public List<OrbitSegment> appendedOrbitSegments;
        public int patchedSegmentCount;
        public int extrapolatedSegmentCount;
        public ConfigNode vesselSnapshot;
        public ConfigNode ghostVisualSnapshot;
        public SurfacePosition? terminalPosition;
        public double? terrainHeightAtEnd;
    }

    internal static class IncompleteBallisticSceneExitFinalizer
    {
        internal delegate bool TryFinalizeDelegate(
            Recording recording,
            Vessel vessel,
            double commitUT,
            out IncompleteBallisticFinalizationResult result);

        internal static TryFinalizeDelegate TryFinalizeOverrideForTesting;

        internal static void ResetForTesting()
        {
            TryFinalizeOverrideForTesting = null;
        }

        internal static bool TryApply(
            Recording recording,
            Vessel vessel,
            double commitUT,
            string logContext)
        {
            var finalize = TryFinalizeOverrideForTesting ?? NoOpTryFinalize;
            if (!finalize(recording, vessel, commitUT, out var result))
                return false;

            if (double.IsNaN(result.terminalUT))
            {
                ParsekLog.Error("Extrapolator",
                    $"{logContext}: rejected incomplete-ballistic finalization for " +
                    $"'{recording?.RecordingId ?? "(null)"}' because terminalUT was NaN");
                return false;
            }

            Apply(recording, result);

            int appendedSegments = result.appendedOrbitSegments?.Count ?? 0;
            ParsekLog.Info("Extrapolator",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: scene-exit finalization applied to '{1}' " +
                    "(patched={2}, extrapolated={3}, appendedSegments={4}, terminal={5}, terminalUT={6:F1}, vesselFound={7})",
                    logContext ?? "SceneExitFinalizer",
                    recording?.RecordingId ?? "(null)",
                    result.patchedSegmentCount,
                    result.extrapolatedSegmentCount,
                    appendedSegments,
                    result.terminalState,
                    result.terminalUT,
                    vessel != null));
            return true;
        }

        private static bool NoOpTryFinalize(
            Recording recording,
            Vessel vessel,
            double commitUT,
            out IncompleteBallisticFinalizationResult result)
        {
            result = default(IncompleteBallisticFinalizationResult);
            return false;
        }

        private static void Apply(
            Recording recording,
            IncompleteBallisticFinalizationResult result)
        {
            if (recording == null)
                return;

            if (result.appendedOrbitSegments != null && result.appendedOrbitSegments.Count > 0)
                recording.OrbitSegments.AddRange(result.appendedOrbitSegments);

            recording.TerminalStateValue = result.terminalState;
            recording.ExplicitEndUT = result.terminalUT;

            if (result.vesselSnapshot != null)
            {
                recording.VesselSnapshot = result.vesselSnapshot.CreateCopy();
                recording.GhostVisualSnapshot = result.ghostVisualSnapshot != null
                    ? result.ghostVisualSnapshot.CreateCopy()
                    : recording.VesselSnapshot.CreateCopy();
            }
            else if (result.ghostVisualSnapshot != null)
            {
                recording.GhostVisualSnapshot = result.ghostVisualSnapshot.CreateCopy();
            }

            if (result.terminalState == TerminalState.Landed
                || result.terminalState == TerminalState.Splashed)
            {
                recording.TerminalPosition = result.terminalPosition;
                recording.TerrainHeightAtEnd = result.terrainHeightAtEnd.HasValue
                    ? result.terrainHeightAtEnd.Value
                    : double.NaN;
            }
            else
            {
                recording.TerminalPosition = null;
                recording.TerrainHeightAtEnd = double.NaN;
            }

            recording.MarkFilesDirty();
        }
    }
}
