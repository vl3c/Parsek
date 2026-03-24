using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Trajectory and visual data for ghost playback.
    /// The ghost playback engine accesses recordings only through this interface,
    /// ensuring it has no knowledge of tree linkage, resource deltas, spawn tracking,
    /// crew data, or any Parsek-specific policy concern.
    ///
    /// Recording implements this interface. Future content pack trajectories will too.
    /// </summary>
    internal interface IPlaybackTrajectory
    {
        // === Trajectory data ===
        List<TrajectoryPoint> Points { get; }
        List<OrbitSegment> OrbitSegments { get; }
        List<TrackSection> TrackSections { get; }
        double StartUT { get; }
        double EndUT { get; }
        int RecordingFormatVersion { get; }

        // === Part/flag events (visual only) ===
        List<PartEvent> PartEvents { get; }
        List<FlagEvent> FlagEvents { get; }

        // === Visual snapshots ===
        ConfigNode GhostVisualSnapshot { get; }
        ConfigNode VesselSnapshot { get; }
        string VesselName { get; }

        // === Loop configuration ===
        bool LoopPlayback { get; }
        double LoopIntervalSeconds { get; }
        LoopTimeUnit LoopTimeUnit { get; }
        uint LoopAnchorVesselId { get; }

        // === Terminal state (for explosion FX) ===
        TerminalState? TerminalStateValue { get; }

        // === Surface hold ===
        SurfacePosition? SurfacePos { get; }

        // === Rendering hints ===
        bool PlaybackEnabled { get; }
        bool IsDebris { get; }
    }
}
