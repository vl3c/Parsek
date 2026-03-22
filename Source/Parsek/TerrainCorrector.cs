namespace Parsek
{
    /// <summary>
    /// Pure static utilities for terrain-aware altitude correction.
    /// Used at spawn time to adjust surface vessel altitude when terrain height
    /// has changed between recording and playback (KSP's procedural terrain
    /// varies between sessions at the same lat/lon).
    /// </summary>
    internal static class TerrainCorrector
    {
        private const string Tag = "TerrainCorrect";

        /// <summary>
        /// Computes the corrected altitude that preserves the vessel's clearance
        /// above terrain, even when terrain height has changed since recording.
        ///
        /// correctedAlt = currentTerrainHeight + (recordedAltitude - recordedTerrainHeight)
        ///
        /// The difference (recordedAltitude - recordedTerrainHeight) is the recorded
        /// clearance — how far above terrain the vessel was when recording ended.
        /// Adding that clearance to the current terrain height gives the correct
        /// altitude for spawning.
        /// </summary>
        internal static double ComputeCorrectedAltitude(
            double currentTerrainHeight,
            double recordedAltitude,
            double recordedTerrainHeight)
        {
            double clearance = recordedAltitude - recordedTerrainHeight;
            double corrected = currentTerrainHeight + clearance;

            // Never spawn below terrain + 0.5m safety margin
            double minAlt = currentTerrainHeight + 0.5;
            if (corrected < minAlt)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeCorrectedAltitude: clamped from {corrected:F1} to {minAlt:F1} (terrain+0.5m floor)");
                corrected = minAlt;
            }

            ParsekLog.Verbose(Tag,
                $"ComputeCorrectedAltitude: currentTerrain={currentTerrainHeight:F1} " +
                $"recordedAlt={recordedAltitude:F1} recordedTerrain={recordedTerrainHeight:F1} " +
                $"clearance={clearance:F1} corrected={corrected:F1}");

            return corrected;
        }

        /// <summary>
        /// Clamps a ghost altitude so it stays above terrain surface.
        /// Pure math — testable without KSP runtime.
        /// Returns ghostAltitude unchanged if already above terrain + minClearance,
        /// otherwise returns terrainHeight + minClearance.
        /// </summary>
        internal static double ClampAltitude(double ghostAltitude, double terrainHeight,
            double minClearance = 0.5)
        {
            double minAlt = terrainHeight + minClearance;
            return ghostAltitude < minAlt ? minAlt : ghostAltitude;
        }

        /// <summary>
        /// Pure decision: should terrain correction apply to this recording?
        ///
        /// Returns true when:
        /// 1. Terminal state is a surface state (Landed or Splashed), AND
        /// 2. recordedTerrainHeight is a valid number (not NaN).
        ///
        /// Returns false for orbital/sub-orbital recordings, destroyed/recovered
        /// vessels, or recordings that lack terrain height data (pre-v7, NaN default).
        /// </summary>
        internal static bool ShouldCorrectTerrain(
            TerminalState? terminalState,
            double recordedTerrainHeight)
        {
            if (!terminalState.HasValue)
            {
                ParsekLog.Verbose(Tag,
                    "ShouldCorrectTerrain: no terminal state — returning false");
                return false;
            }

            var ts = terminalState.Value;
            bool isSurface = ts == TerminalState.Landed || ts == TerminalState.Splashed;
            if (!isSurface)
            {
                ParsekLog.Verbose(Tag,
                    $"ShouldCorrectTerrain: terminal={ts} is not surface — returning false");
                return false;
            }

            if (double.IsNaN(recordedTerrainHeight))
            {
                ParsekLog.Verbose(Tag,
                    "ShouldCorrectTerrain: recordedTerrainHeight is NaN — returning false");
                return false;
            }

            ParsekLog.Verbose(Tag,
                $"ShouldCorrectTerrain: terminal={ts} terrainHeight={recordedTerrainHeight:F1} — returning true");
            return true;
        }
    }
}
