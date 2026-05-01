using System.Globalization;

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
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        internal const double DefaultTailLiftRampSeconds = 30.0;
        internal const double TailLiftMinAbsDeltaMeters = 2.0;

        internal readonly struct TailLiftPlan
        {
            public readonly bool Active;
            public readonly double TerminalUT;
            public readonly double RampStartUT;
            public readonly double TerrainDelta;

            internal TailLiftPlan(double terminalUT, double rampStartUT, double terrainDelta)
            {
                Active = true;
                TerminalUT = terminalUT;
                RampStartUT = rampStartUT;
                TerrainDelta = terrainDelta;
            }

            internal static TailLiftPlan Inactive => default(TailLiftPlan);
        }

        // ComputeCorrectedAltitude removed (#309): the terrain-relative clearance
        // model ("currentTerrain + (recordedAlt - recordedTerrain)") buries any
        // vessel recorded on a mesh object (Island Airfield runway, launchpad,
        // KSC buildings) because body.TerrainAltitude() is PQS-only and returns
        // the raw planetary surface UNDER placed colliders. Replaced by
        // "trust the recorded altitude + underground safety floor" semantics in
        // VesselSpawner.ClampAltitudeForLanded and ParsekFlight.ApplyLandedGhostClearance.
        // The recorded altitude is the authoritative surface position — KSP's own
        // CheckGroundCollision will settle against real colliders on load.

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

        internal static TailLiftPlan BuildTailLiftPlan(
            TerminalState? terminalState,
            double terrainHeightAtEnd,
            double currentTerrainAtEnd,
            double terminalUT,
            double rampSeconds,
            double minDeltaMeters)
        {
            if (!terminalState.HasValue)
                return TailLiftPlan.Inactive;

            TerminalState ts = terminalState.Value;
            bool isSurfaceTerminal = ts == TerminalState.Landed
                || ts == TerminalState.Splashed
                || ts == TerminalState.Recovered;
            if (!isSurfaceTerminal)
                return TailLiftPlan.Inactive;

            if (double.IsNaN(terrainHeightAtEnd) || double.IsNaN(currentTerrainAtEnd))
                return TailLiftPlan.Inactive;

            if (rampSeconds <= 0.0)
                return TailLiftPlan.Inactive;

            double delta = currentTerrainAtEnd - terrainHeightAtEnd;
            if (System.Math.Abs(delta) < minDeltaMeters)
                return TailLiftPlan.Inactive;

            return new TailLiftPlan(terminalUT, terminalUT - rampSeconds, delta);
        }

        internal static double EvaluateTailLift(double pointUT, in TailLiftPlan plan)
        {
            if (!plan.Active)
                return 0.0;
            if (pointUT >= plan.TerminalUT)
                return plan.TerrainDelta;
            if (pointUT <= plan.RampStartUT)
                return 0.0;

            double span = plan.TerminalUT - plan.RampStartUT;
            if (span <= 0.0)
                return pointUT >= plan.TerminalUT ? plan.TerrainDelta : 0.0;

            double fraction = (pointUT - plan.RampStartUT) / span;
            return plan.TerrainDelta * fraction;
        }

        /// <summary>
        /// Pure decision: should terrain correction apply to this recording?
        ///
        /// Returns true when:
        /// 1. Terminal state is a surface state (Landed or Splashed), AND
        /// 2. recordedTerrainHeight is a valid number (not NaN).
        ///
        /// Returns false for orbital/sub-orbital recordings, destroyed/recovered
        /// vessels, or recordings that lack terrain height data (NaN default).
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
                $"ShouldCorrectTerrain: terminal={ts} terrainHeight={recordedTerrainHeight.ToString("F1", IC)} — returning true");
            return true;
        }
    }
}
