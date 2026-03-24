using System.Collections.Generic;

namespace Parsek.Tests
{
    /// <summary>
    /// Reusable mock implementing IPlaybackTrajectory without depending on Recording.
    /// If the engine ever casts to Recording, tests using this mock will throw —
    /// verifying interface isolation.
    /// </summary>
    internal class MockTrajectory : IPlaybackTrajectory
    {
        public List<TrajectoryPoint> Points { get; set; } = new List<TrajectoryPoint>();
        public List<OrbitSegment> OrbitSegments { get; set; } = new List<OrbitSegment>();
        public List<TrackSection> TrackSections { get; set; } = new List<TrackSection>();
        public double StartUT => Points.Count > 0 ? Points[0].ut : 0;
        public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut : 0;
        public int RecordingFormatVersion { get; set; } = 6;
        public List<PartEvent> PartEvents { get; set; } = new List<PartEvent>();
        public List<FlagEvent> FlagEvents { get; set; } = new List<FlagEvent>();
        public ConfigNode GhostVisualSnapshot { get; set; }
        public ConfigNode VesselSnapshot { get; set; }
        public string VesselName { get; set; } = "MockVessel";
        public bool LoopPlayback { get; set; }
        public double LoopIntervalSeconds { get; set; } = 10;
        public LoopTimeUnit LoopTimeUnit { get; set; }
        public uint LoopAnchorVesselId { get; set; }
        public TerminalState? TerminalStateValue { get; set; }
        public SurfacePosition? SurfacePos { get; set; }
        public bool PlaybackEnabled { get; set; } = true;
        public bool IsDebris { get; set; }

        /// <summary>
        /// Helper: add two points spanning [startUT, endUT] for a minimal valid trajectory.
        /// </summary>
        public MockTrajectory WithTimeRange(double startUT, double endUT)
        {
            Points.Clear();
            Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });
            Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });
            return this;
        }

        /// <summary>
        /// Helper: configure as a looping trajectory.
        /// </summary>
        public MockTrajectory WithLoop(double intervalSeconds = 10, LoopTimeUnit unit = LoopTimeUnit.Sec)
        {
            LoopPlayback = true;
            LoopIntervalSeconds = intervalSeconds;
            LoopTimeUnit = unit;
            return this;
        }
    }
}
