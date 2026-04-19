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
        bool HasOrbitSegments { get; }
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

        // === Identity ===
        // Stable GUID-style id for dedupe of per-recording diagnostics (e.g. the clamp
        // warning in GhostPlaybackLogic.ResolveLoopInterval). May be null/empty on
        // transient or test fixtures; consumers must fall back to VesselName.
        string RecordingId { get; }

        // === Loop configuration ===
        bool LoopPlayback { get; }
        double LoopIntervalSeconds { get; }
        LoopTimeUnit LoopTimeUnit { get; }
        uint LoopAnchorVesselId { get; }
        double LoopStartUT { get; }
        double LoopEndUT { get; }

        // === Terminal state (for explosion FX) ===
        TerminalState? TerminalStateValue { get; }

        // === Surface hold ===
        SurfacePosition? SurfacePos { get; }
        double TerrainHeightAtEnd { get; }

        // === Rendering hints ===
        bool PlaybackEnabled { get; }
        bool IsDebris { get; }

        // === Loop sync (debris follows parent's loop clock) ===
        int LoopSyncParentIdx { get; set; }

        // === Terminal orbit (for ghost map presence) ===
        string TerminalOrbitBody { get; }
        double TerminalOrbitSemiMajorAxis { get; }
        double TerminalOrbitEccentricity { get; }
        double TerminalOrbitInclination { get; }
        double TerminalOrbitLAN { get; }
        double TerminalOrbitArgumentOfPeriapsis { get; }
        double TerminalOrbitMeanAnomalyAtEpoch { get; }
        double TerminalOrbitEpoch { get; }
        RecordingEndpointPhase EndpointPhase { get; }
        string EndpointBodyName { get; }
    }
}
