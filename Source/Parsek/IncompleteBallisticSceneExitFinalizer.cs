using System;
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
        public TerminalState? terminalState;
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

        internal static TryFinalizeDelegate TryFinalizeHook;
        internal static TryFinalizeDelegate TryFinalizeOverrideForTesting;

        internal static void ResetForTesting()
        {
            TryFinalizeHook = null;
            TryFinalizeOverrideForTesting = null;
        }

        internal static bool TryApply(
            Recording recording,
            Vessel vessel,
            double commitUT,
            string logContext)
        {
            var finalize = TryFinalizeHook ?? TryFinalizeOverrideForTesting ?? NoOpTryFinalize;
            bool usingHook = TryFinalizeHook != null;
            if (!finalize(recording, vessel, commitUT, out var result))
            {
                if (usingHook)
                {
                    double currentEndUT = recording != null ? recording.EndUT : double.NaN;
                    ParsekLog.Verbose("Extrapolator",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}: incomplete-ballistic finalization hook declined for '{1}' " +
                            "(commitUT={2:F1}, currentEndUT={3:F1}, vesselFound={4})",
                            logContext ?? "SceneExitFinalizer",
                            recording?.RecordingId ?? "(null)",
                            commitUT,
                            currentEndUT,
                            vessel != null));
                }
                return false;
            }

            if (!ValidateResult(recording, commitUT, result, logContext))
                return false;

            Apply(recording, result, logContext);

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
                    result.terminalState.Value,
                    result.terminalUT,
                    vessel != null));
            return true;
        }

        private static bool ValidateResult(
            Recording recording,
            double commitUT,
            IncompleteBallisticFinalizationResult result,
            string logContext)
        {
            string context = logContext ?? "SceneExitFinalizer";
            string recordingId = recording?.RecordingId ?? "(null)";
            if (!result.terminalState.HasValue)
            {
                ParsekLog.Error("Extrapolator",
                    $"{context}: rejected incomplete-ballistic finalization for " +
                    $"'{recordingId}' because terminalState was unset/default");
                return false;
            }

            if (!Enum.IsDefined(typeof(TerminalState), result.terminalState.Value))
            {
                ParsekLog.Error("Extrapolator",
                    $"{context}: rejected incomplete-ballistic finalization for " +
                    $"'{recordingId}' because terminalState=" +
                    $"{(int)result.terminalState.Value} was invalid");
                return false;
            }

            if (double.IsNaN(result.terminalUT))
            {
                ParsekLog.Error("Extrapolator",
                    $"{context}: rejected incomplete-ballistic finalization for " +
                    $"'{recordingId}' because terminalUT was NaN");
                return false;
            }

            double currentEndUT = recording != null ? recording.EndUT : double.NaN;
            double floorUT = GetTerminalUtFloor(commitUT, currentEndUT);
            if (!double.IsNaN(floorUT) && result.terminalUT < floorUT)
            {
                ParsekLog.Error("Extrapolator",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: rejected incomplete-ballistic finalization for '{1}' because terminalUT={2:F1} " +
                        "moved backward before max(commitUT={3:F1}, currentEndUT={4:F1})",
                        context,
                        recordingId,
                        result.terminalUT,
                        commitUT,
                        currentEndUT));
                return false;
            }

            return true;
        }

        private static double GetTerminalUtFloor(double commitUT, double currentEndUT)
        {
            if (double.IsNaN(commitUT))
                return currentEndUT;
            if (double.IsNaN(currentEndUT))
                return commitUT;
            return Math.Max(commitUT, currentEndUT);
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
            IncompleteBallisticFinalizationResult result,
            string logContext)
        {
            if (recording == null)
                return;

            if (result.appendedOrbitSegments != null && result.appendedOrbitSegments.Count > 0)
                recording.OrbitSegments.AddRange(result.appendedOrbitSegments);

            recording.TerminalStateValue = result.terminalState.Value;
            recording.ExplicitEndUT = result.terminalUT;

            bool ghostOnlySnapshot = result.vesselSnapshot == null && result.ghostVisualSnapshot != null;
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
                ParsekLog.Verbose("Extrapolator",
                    $"{logContext ?? "SceneExitFinalizer"}: applied ghost-only scene-exit finalization for " +
                    $"'{recording.RecordingId}' without a vessel snapshot");
            }

            if (result.terminalState.Value == TerminalState.Landed
                || result.terminalState.Value == TerminalState.Splashed)
            {
                if (result.terminalPosition.HasValue)
                {
                    recording.TerminalPosition = result.terminalPosition;
                    recording.TerrainHeightAtEnd = result.terrainHeightAtEnd.HasValue
                        ? result.terrainHeightAtEnd.Value
                        : double.NaN;
                }
                else if (ghostOnlySnapshot)
                {
                    bool hadSurfaceMetadata = recording.TerminalPosition.HasValue
                        || !double.IsNaN(recording.TerrainHeightAtEnd);
                    ParsekLog.Warn("Extrapolator",
                        $"{logContext ?? "SceneExitFinalizer"}: ghost-only surface finalization for " +
                        $"'{recording.RecordingId}' supplied no terminalPosition/terrainHeight — " +
                        (hadSurfaceMetadata
                            ? "keeping existing surface metadata"
                            : "surface metadata remains unavailable"));
                }
                else
                {
                    recording.TerminalPosition = null;
                    recording.TerrainHeightAtEnd = double.NaN;
                }
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
